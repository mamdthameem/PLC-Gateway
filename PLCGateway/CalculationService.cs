using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PLCGateway.Models;

/// <summary>
/// Computes all 15 KPI metrics and upserts them for every period
/// (realtime, hour, shift, day, week, month, year, cumulative) simultaneously.
/// </summary>
public class CalculationService
{
    private readonly DatabaseService _dbService;
    private readonly ILogger<CalculationService> _logger;

    // Energy constants — stored here so operator can verify
    private readonly double _voltageV;
    private readonly double _powerFactor;
    private readonly int _activeImpellerCount;

    // Maintenance threshold
    private readonly double _spareLifeBlastHours;

    // PLC tag parameter names (must match appsettings.json Tags[].Name exactly)
    private const string TAG_BLAST        = "Blast ON/OFF";
    private const string TAG_REBLAST      = "Reblast ON/OFF";
    private const string TAG_MACHINE_ST   = "Machine status";
    private const string TAG_SHOT_REFILL  = "Refil shots weight";
    private const string TAG_TONNAGE      = "Tonnage";
    // Casting weights per cycle (4 metals)
    private static readonly string[] TAG_CAST_WEIGHTS =
    {
        "Casting metal 1 weight",
        "Casting metal 2 weight",
        "Casting metal 3 weight",
        "Casting metal 4 weight"
    };
    // Motor current tags (imp 1..10)
    private static readonly string[] TAG_CURRENT =
    {
        "Current_imp_1","Current_imp_2","Current_imp_3","Current_imp_4","Current_imp_5",
        "Current_imp_6","Current_imp_7","Current_imp_8","Current_imp_9","Current_imp_10"
    };
    // Spare trigger tags per impeller (10 impellers × 14 parts each — index 0)
    // We check trigger[0] as the representative trigger per impeller
    private static readonly string[] TAG_SPARE_TRIGGER =
    {
        "Spares trigger imp1[0]","Spares trigger imp2[0]","Spares trigger imp3[0]",
        "Spares trigger imp4[0]","Spares trigger imp5[0]","Spares trigger imp6[0]",
        "Spares trigger imp7[0]","Spares trigger imp8[0]","Spares trigger imp9[0]",
        "Spares trigger imp10[0]"
    };
    private static readonly string[] TAG_RUNHOUR =
    {
        "Spares_Runhour_imp1[0]","Spares_Runhour_imp2[0]","Spares_Runhour_imp3[0]",
        "Spares_Runhour_imp4[0]","Spares_Runhour_imp5[0]","Spares_Runhour_imp6[0]",
        "Spares_Runhour_imp7[0]","Spares_Runhour_imp8[0]","Spares_Runhour_imp9[0]",
        "Spares_Runhour_imp10[0]"
    };

    public CalculationService(
        DatabaseService dbService,
        ILogger<CalculationService> logger,
        IConfiguration configuration)
    {
        _dbService = dbService;
        _logger = logger;
        _voltageV            = configuration.GetValue<double>("EnergyCalculation:SupplyVoltageV", 415);
        _powerFactor         = configuration.GetValue<double>("EnergyCalculation:PowerFactor", 0.85);
        _activeImpellerCount = configuration.GetValue<int>("EnergyCalculation:ActiveImpellerCount", 10);
        _spareLifeBlastHours = configuration.GetValue<double>("MaintenanceThresholds:SpareLifeBlastHours", 500);
    }

    // ────────────────────────────────────────────────────────────────────────
    // MAIN ENTRY POINT
    // Called by AggregationService every minute (and on event triggers).
    // Computes ALL metrics for ALL periods and upserts them.
    // ────────────────────────────────────────────────────────────────────────
    public async Task ComputeAndStoreAllMetricsAsync()
    {
        var now = DateTime.Now;

        // Define all period windows to compute
        var periods = BuildPeriodWindows(now);

        try
        {
            // Run all 15 calculations in parallel per period
            var tasks = new List<Task>();
            foreach (var p in periods)
            {
                tasks.Add(ComputePeriodMetricsAsync(p.periodType, p.start, p.end, p.periodLabel));
            }
            // Cumulative (all-time) — no start boundary
            tasks.Add(ComputeCumulativeMetricsAsync());
            // Real-time current values
            tasks.Add(ComputeRealTimeMetricsAsync());

            await Task.WhenAll(tasks);

            _logger.LogDebug("All metrics computed and stored for {time}", now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeAndStoreAllMetricsAsync");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PERIOD WINDOWS
    // ────────────────────────────────────────────────────────────────────────
    private List<(string periodType, DateTime start, DateTime end, DateTime periodLabel)> BuildPeriodWindows(DateTime now)
    {
        var list = new List<(string, DateTime, DateTime, DateTime)>();

        // Hour: current hour window
        var hourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0);
        list.Add(("hour", hourStart, now, hourStart));

        // Day
        var dayStart = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0);
        list.Add(("day", dayStart, now, dayStart));

        // Week (Monday-based)
        int daysFromMonday = ((int)now.DayOfWeek + 6) % 7;
        var weekStart = now.Date.AddDays(-daysFromMonday);
        list.Add(("week", weekStart, now, weekStart));

        // Month
        var monthStart = new DateTime(now.Year, now.Month, 1);
        list.Add(("month", monthStart, now, monthStart));

        // Year
        var yearStart = new DateTime(now.Year, 1, 1);
        list.Add(("year", yearStart, now, yearStart));

        return list;
    }

    // ────────────────────────────────────────────────────────────────────────
    // REAL-TIME METRICS (live snapshot from Tier 1 current values)
    // ────────────────────────────────────────────────────────────────────────
    private async Task ComputeRealTimeMetricsAsync()
    {
        try
        {
            // 1. Machine status — direct from Tier 1
            var machineStatusVal = await _dbService.GetCurrentValueAsync("DB60.DBB0");
            if (machineStatusVal != null)
                await Upsert("machine_status", ParseDecimal(machineStatusVal.Value), "realtime", null);

            // 13. Amps per impeller — live current readings
            double totalKw = 0;
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                var currentTag = await _dbService.GetCurrentValueAsync(GetImpellerCurrentAddress(i));
                if (currentTag == null) continue;

                double amps = ParseDouble(currentTag.Value);
                // kW = √3 × V × I × PF / 1000
                double kw = Math.Sqrt(3) * _voltageV * amps * _powerFactor / 1000.0;
                totalKw += kw;

                int impNo = i + 1;
                await Upsert($"amps_imp_{impNo}", (decimal)Math.Round(amps, 3), "realtime", null);
                await Upsert($"energy_kw_imp_{impNo}", (decimal)Math.Round(kw, 3), "realtime", null);
            }
            await Upsert("energy_kw_total", (decimal)Math.Round(totalKw, 3), "realtime", null);

            // 12. Maintenance pop-up flags per impeller
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                int impNo = i + 1;
                var triggerTag = await _dbService.GetCurrentValueAsync(GetSparesTriggerAddress(i));
                bool triggered = triggerTag?.Value == "1" || triggerTag?.Value?.ToLower() == "true";
                await Upsert($"maintenance_alert_imp_{impNo}", triggered ? 1m : 0m, "realtime", null);

                var runHourTag = await _dbService.GetCurrentValueAsync(GetRunHourAddress(i));
                double runHours = ParseDouble(runHourTag?.Value);
                await Upsert($"spare_runhour_imp_{impNo}", (decimal)Math.Round(runHours, 2), "realtime", null);
            }

            // 11. Last refill time — timestamp stored as Unix ms in metric value
            var lastRefill = await _dbService.GetLatestHistoricalAsync(TAG_SHOT_REFILL);
            if (lastRefill != null)
            {
                // Store as epoch seconds so dashboard can format it
                long epochSec = ((DateTimeOffset)lastRefill.Timestamp).ToUnixTimeSeconds();
                await Upsert("last_refill_epoch_sec", (decimal)epochSec, "realtime", null,
                    $"{{\"timestamp\":\"{lastRefill.Timestamp:yyyy-MM-dd HH:mm:ss}\"}}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeRealTimeMetricsAsync");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // PERIOD METRICS (hour / day / week / month / year)
    // ────────────────────────────────────────────────────────────────────────
    private async Task ComputePeriodMetricsAsync(string periodType, DateTime start, DateTime end, DateTime periodLabel)
    {
        try
        {
            // ── Shift handling ─────────────────────────────────────────────
            // Shift starts when Machine status goes from 0 → non-zero.
            // For non-shift period types, shiftStart = period start.
            DateTime shiftStart = start;
            if (periodType == "hour" || periodType == "day" || periodType == "week" || periodType == "month" || periodType == "year")
            {
                var detectedShift = await _dbService.GetShiftStartAsync(start);
                if (detectedShift.HasValue && detectedShift.Value <= end)
                    shiftStart = detectedShift.Value;
            }
            double availableSeconds = (end - shiftStart).TotalSeconds;
            if (availableSeconds <= 0) availableSeconds = 1; // guard zero-division

            // ── 2. Machine Utility (%) ─────────────────────────────────────
            double blastSec = await ComputeBlastTimeSecondsAsync(shiftStart, end);
            double machineUtility = blastSec / availableSeconds * 100.0;
            machineUtility = Math.Min(machineUtility, 100.0);
            await Upsert("machine_utility_pct", (decimal)Math.Round(machineUtility, 2), periodType, periodLabel);

            // ── 6. Total Blast Time (seconds) ─────────────────────────────
            await Upsert("blast_time_sec", (decimal)Math.Round(blastSec, 1), periodType, periodLabel);

            // ── 10. Cycle Count ────────────────────────────────────────────
            var (cycleCount, reblastCount) = await ComputeCycleCountAsync(start, end);
            await Upsert("cycle_count", cycleCount, periodType, periodLabel);
            await Upsert("reblast_count", reblastCount, periodType, periodLabel);

            // ── 3. Production Quantity (kg) ────────────────────────────────
            double productionKg = await ComputeProductionKgAsync(start, end);
            await Upsert("production_qty_kg", (decimal)Math.Round(productionKg, 2), periodType, periodLabel);

            // ── 4 & 5. Energy per impeller + total, Energy per Casting ──────
            double totalKwh = 0;
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                int impNo = i + 1;
                double kwh = await ComputeEnergyKwhAsync(i, start, end);
                totalKwh += kwh;
                await Upsert($"energy_kwh_imp_{impNo}", (decimal)Math.Round(kwh, 3), periodType, periodLabel);
            }
            await Upsert("energy_kwh_total", (decimal)Math.Round(totalKwh, 3), periodType, periodLabel);

            // Energy per casting (kWh/kg)
            double energyPerCasting = productionKg > 0 ? totalKwh / productionKg : 0;
            await Upsert("energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4), periodType, periodLabel);

            // ── 7. Effective Shots Usage ───────────────────────────────────
            double shotsUsageIndex = productionKg > 0
                ? await ComputeEffectiveShotsUsageAsync(start, end) / productionKg
                : 0;
            await Upsert("effective_shots_usage", (decimal)Math.Round(shotsUsageIndex, 4), periodType, periodLabel);

            // ── 8. Average Shot Refill Time ────────────────────────────────
            double avgRefillSec = await ComputeAvgRefillTimeSecondsAsync(start, end);
            await Upsert("avg_shot_refill_time_sec", (decimal)Math.Round(avgRefillSec, 1), periodType, periodLabel);

            // ── 9. Chamber-wise Utilization (per impeller) ────────────────
            // Each impeller's "active time" = duration where its current > threshold during blast
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                int impNo = i + 1;
                double impActiveKwh = await ComputeEnergyKwhAsync(i, start, end);
                // Utilization = impeller contributed energy / total energy (proportional)
                double impUtil = totalKwh > 0 ? impActiveKwh / totalKwh * machineUtility : 0;
                await Upsert($"chamber_util_pct_imp_{impNo}", (decimal)Math.Round(impUtil, 2), periodType, periodLabel);
            }

            // ── 8. Average Shot Refill stored above ────────────────────────
            // ── 15. Rework (reblast count already stored above) ───────────

            _logger.LogDebug("Period metrics stored: {type} {label}", periodType, periodLabel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing period metrics for {type}", periodType);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // CUMULATIVE METRICS (all-time, no time window)
    // ────────────────────────────────────────────────────────────────────────
    private async Task ComputeCumulativeMetricsAsync()
    {
        try
        {
            var epoch = new DateTime(2000, 1, 1);
            var now = DateTime.Now;

            double blastSec = await ComputeBlastTimeSecondsAsync(epoch, now);
            await Upsert("blast_time_sec", (decimal)Math.Round(blastSec, 1), "cumulative", null);

            var (cycles, reblasts) = await ComputeCycleCountAsync(epoch, now);
            await Upsert("cycle_count", cycles, "cumulative", null);
            await Upsert("reblast_count", reblasts, "cumulative", null);

            double prodKg = await ComputeProductionKgAsync(epoch, now);
            await Upsert("production_qty_kg", (decimal)Math.Round(prodKg, 2), "cumulative", null);

            double totalKwh = 0;
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                double kwh = await ComputeEnergyKwhAsync(i, epoch, now);
                totalKwh += kwh;
                await Upsert($"energy_kwh_imp_{i + 1}", (decimal)Math.Round(kwh, 3), "cumulative", null);
            }
            await Upsert("energy_kwh_total", (decimal)Math.Round(totalKwh, 3), "cumulative", null);

            double shotsTotal = await ComputeEffectiveShotsUsageAsync(epoch, now);
            await Upsert("effective_shots_usage", (decimal)Math.Round(shotsTotal, 4), "cumulative", null);

            double avgRefill = await ComputeAvgRefillTimeSecondsAsync(epoch, now);
            await Upsert("avg_shot_refill_time_sec", (decimal)Math.Round(avgRefill, 1), "cumulative", null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeCumulativeMetricsAsync");
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // SHIFT METRICS — called separately by AggregationService on shift end
    // ────────────────────────────────────────────────────────────────────────
    public async Task ComputeShiftMetricsAsync(DateTime shiftStart, DateTime shiftEnd)
    {
        await ComputePeriodMetricsAsync("shift", shiftStart, shiftEnd, shiftStart);
    }

    // ════════════════════════════════════════════════════════════════════════
    // FORMULA IMPLEMENTATIONS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Total seconds where Blast ON/OFF = 1 within the window.
    /// Reconstructed from STATE_CHANGE records in Tier 2.
    /// </summary>
    private async Task<double> ComputeBlastTimeSecondsAsync(DateTime start, DateTime end)
    {
        var records = await _dbService.GetStateChangesAsync(TAG_BLAST, start, end);
        return ComputeOnTimeSeconds(records, start, end, isOn: v => v == "1" || v?.ToLower() == "true");
    }

    /// <summary>
    /// Blast cycle count = number of 0→1 rising edges on Blast ON/OFF.
    /// Reblast count = number of 0→1 rising edges on Reblast ON/OFF.
    /// Returns (cycleCount, reblastCount).
    /// </summary>
    private async Task<(decimal cycleCount, decimal reblastCount)> ComputeCycleCountAsync(DateTime start, DateTime end)
    {
        var blastRecords   = await _dbService.GetStateChangesAsync(TAG_BLAST,   start, end);
        var reblastRecords = await _dbService.GetStateChangesAsync(TAG_REBLAST, start, end);

        int blastCycles   = CountRisingEdges(blastRecords);
        int reblastCycles = CountRisingEdges(reblastRecords);

        return ((decimal)blastCycles, (decimal)reblastCycles);
    }

    /// <summary>
    /// Production quantity = sum of all casting metal weights logged in the window.
    /// Each blast cycle resets the weight tags, so we sum all COV records.
    /// </summary>
    private async Task<double> ComputeProductionKgAsync(DateTime start, DateTime end)
    {
        double total = 0;
        foreach (var tagName in TAG_CAST_WEIGHTS)
        {
            var records = await _dbService.GetStateChangesAsync(tagName, start, end);
            // Each COV record is a new cycle's weight reading — sum them all
            foreach (var r in records)
            {
                double val = ParseDouble(r.Value);
                if (val > 0) total += val;
            }
        }
        return total;
    }

    /// <summary>
    /// Energy (kWh) for one impeller over the window.
    /// Reconstructed from COV records of current — integrate amps × voltage × √3 × PF over time.
    /// kWh = Σ [ (kW_i × Δt_seconds) / 3600 ]
    /// </summary>
    private async Task<double> ComputeEnergyKwhAsync(int impellerIndex, DateTime start, DateTime end)
    {
        string tagName = TAG_CURRENT[impellerIndex];
        var records = await _dbService.GetStateChangesAsync(tagName, start, end);

        if (records.Count == 0) return 0;

        double totalKwh = 0;
        DateTime prevTime = start;
        double prevAmps = 0;

        foreach (var r in records)
        {
            double dtHours = (r.Timestamp - prevTime).TotalSeconds / 3600.0;
            double kw = Math.Sqrt(3) * _voltageV * prevAmps * _powerFactor / 1000.0;
            totalKwh += kw * dtHours;

            prevAmps = ParseDouble(r.Value);
            prevTime = r.Timestamp;
        }

        // Final segment: from last record to end of window
        double dtFinalHours = (end - prevTime).TotalSeconds / 3600.0;
        double kwFinal = Math.Sqrt(3) * _voltageV * prevAmps * _powerFactor / 1000.0;
        totalKwh += kwFinal * dtFinalHours;

        return Math.Max(totalKwh, 0);
    }

    /// <summary>
    /// Effective Shots Usage = total shots weight refilled in the window.
    /// Caller divides by production kg to get the index (kg shots / kg casting).
    /// </summary>
    private async Task<double> ComputeEffectiveShotsUsageAsync(DateTime start, DateTime end)
    {
        var records = await _dbService.GetStateChangesAsync(TAG_SHOT_REFILL, start, end);
        double total = 0;
        foreach (var r in records)
        {
            double val = ParseDouble(r.Value);
            if (val > 0) total += val;
        }
        return total;
    }

    /// <summary>
    /// Average Shot Refill Time = average gap (seconds) between consecutive refill events.
    /// </summary>
    private async Task<double> ComputeAvgRefillTimeSecondsAsync(DateTime start, DateTime end)
    {
        var records = await _dbService.GetStateChangesAsync(TAG_SHOT_REFILL, start, end);
        if (records.Count < 2) return 0;

        double totalGap = 0;
        for (int i = 1; i < records.Count; i++)
            totalGap += (records[i].Timestamp - records[i - 1].Timestamp).TotalSeconds;

        return totalGap / (records.Count - 1);
    }

    // ════════════════════════════════════════════════════════════════════════
    // HELPERS
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reconstructs total ON-time (seconds) from a list of STATE_CHANGE records.
    /// Handles windows that start mid-state by reading the first record's previous_value.
    /// </summary>
    private static double ComputeOnTimeSeconds(
        List<PlcHistoricalData> records,
        DateTime windowStart,
        DateTime windowEnd,
        Func<string?, bool> isOn)
    {
        if (records.Count == 0) return 0;

        double totalSec = 0;
        // Determine state at window start using first record's previous_value
        bool currentlyOn = isOn(records[0].PreviousValue);
        DateTime segmentStart = windowStart;

        foreach (var r in records)
        {
            bool newState = isOn(r.Value);
            if (currentlyOn && !newState)
            {
                // Transition OFF: measure duration
                totalSec += (r.Timestamp - segmentStart).TotalSeconds;
            }
            else if (!currentlyOn && newState)
            {
                // Transition ON: record start
                segmentStart = r.Timestamp;
            }
            currentlyOn = newState;
        }

        // If still ON at end of window
        if (currentlyOn)
            totalSec += (windowEnd - segmentStart).TotalSeconds;

        return Math.Max(totalSec, 0);
    }

    /// <summary>
    /// Count 0→1 (false→true) rising edges in a list of BOOL records.
    /// </summary>
    private static int CountRisingEdges(List<PlcHistoricalData> records)
    {
        int count = 0;
        foreach (var r in records)
        {
            bool prev = r.PreviousValue == "1" || r.PreviousValue?.ToLower() == "true";
            bool curr = r.Value == "1" || r.Value?.ToLower() == "true";
            if (!prev && curr) count++;
        }
        return count;
    }

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static decimal ParseDecimal(string? s)
        => (decimal)ParseDouble(s);

    // Address helpers — matches addresses in appsettings.json
    private static string GetImpellerCurrentAddress(int zeroIndex) => zeroIndex switch
    {
        0 => "DB60.DBD1650", 1 => "DB60.DBD1654", 2 => "DB60.DBD1658", 3 => "DB60.DBD1662",
        4 => "DB60.DBD1666", 5 => "DB60.DBD1670", 6 => "DB60.DBD1674", 7 => "DB60.DBD1678",
        8 => "DB60.DBD1682", _ => "DB60.DBD1686"
    };

    private static string GetSparesTriggerAddress(int zeroIndex) => zeroIndex switch
    {
        0 => "DB60.DBX1050.0", 1 => "DB60.DBX1052.0", 2 => "DB60.DBX1054.0", 3 => "DB60.DBX1056.0",
        4 => "DB60.DBX1058.0", 5 => "DB60.DBX1060.0", 6 => "DB60.DBX1062.0", 7 => "DB60.DBX1064.0",
        8 => "DB60.DBX1066.0", _ => "DB60.DBX1068.0"
    };

    private static string GetRunHourAddress(int zeroIndex) => zeroIndex switch
    {
        0 => "DB60.DBD1070", 1 => "DB60.DBD1126", 2 => "DB60.DBD1182", 3 => "DB60.DBD1238",
        4 => "DB60.DBD1294", 5 => "DB60.DBD1350", 6 => "DB60.DBD1406", 7 => "DB60.DBD1462",
        8 => "DB60.DBD1518", _ => "DB60.DBD1574"
    };

    private Task Upsert(string name, decimal? value, string periodType, DateTime? periodLabel, string? meta = null)
        => _dbService.UpsertCalculatedMetricAsync(name, value, periodType, periodLabel, meta);
}
