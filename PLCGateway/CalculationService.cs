using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PLCGateway.Models;

public class CalculationService
{
    private readonly DatabaseService _db;
    private readonly ILogger<CalculationService> _logger;
    private readonly int _activeImpellerCount;

    private const string TAG_BLAST       = "Blast ON/OFF";
    private const string TAG_MACHINE_ST  = "Machine status";
    private const string TAG_SHOT_REFILL = "Refil shots weight";
    private const string TAG_TONNAGE     = "Tonnage";
    private const string ADDR_MACHINE_ST = "DB60.DBB0";

    private static readonly string[] TAG_CURRENT =
    {
        "Current_imp_1","Current_imp_2","Current_imp_3","Current_imp_4","Current_imp_5",
        "Current_imp_6","Current_imp_7","Current_imp_8","Current_imp_9","Current_imp_10"
    };

    public CalculationService(
        DatabaseService db,
        ILogger<CalculationService> logger,
        IConfiguration configuration)
    {
        _db                  = db;
        _logger              = logger;
        _activeImpellerCount = configuration.GetValue<int>("EnergyCalculation:ActiveImpellerCount", 10);
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 1 — Lifetime parameters (called every minute by AggregationService)
    // ════════════════════════════════════════════════════════════════════════

    public async Task ComputeLifetimeParametersAsync()
    {
        try
        {
            var epoch = new DateTime(2000, 1, 1);
            var now   = DateTime.Now;

            // #1 machine_status — live from Tier 1
            var machineStatusRec = await _db.GetCurrentValueAsync(ADDR_MACHINE_ST);
            decimal machineStatusVal = machineStatusRec?.Value != null && machineStatusRec.Value != "0" ? 1m : 0m;
            await _db.UpsertLifetimeParameterAsync("machine_status", machineStatusVal);

            // #3 production_qty_kg — latest raw Tonnage value from Tier 1 (PLC running accumulator)
            var tonnageRec = await _db.GetCurrentValueByNameAsync(TAG_TONNAGE);
            double productionKg = ParseDouble(tonnageRec?.Value);
            await _db.UpsertLifetimeParameterAsync("production_qty_kg", (decimal)Math.Round(productionKg, 2));

            // #2, #6, #10, reblast_count — shared window parameters
            var windowParams = await ComputeWindowParametersAsync(epoch, now);
            foreach (var kv in windowParams)
                await _db.UpsertLifetimeParameterAsync(kv.Key, kv.Value);

            // #4 energy_kwh_total — per-cycle avg amps × duration hours, summed across impellers and cycles
            var allCycles = await _db.GetCyclesByTimeRangeAsync(epoch, now);
            double totalKwh = await ComputeEnergyKwhFromCyclesAsync(allCycles);
            await _db.UpsertLifetimeParameterAsync("energy_kwh_total", (decimal)Math.Round(totalKwh, 3));

            // #5 energy_per_casting_kwh_kg
            double energyPerCasting = productionKg > 0 ? totalKwh / productionKg : 0;
            await _db.UpsertLifetimeParameterAsync("energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4));

            // #11 last_refill_epoch_sec
            var lastRefill = await _db.GetLatestHistoricalAsync(TAG_SHOT_REFILL);
            if (lastRefill != null)
            {
                long epochSec = ((DateTimeOffset)lastRefill.Timestamp).ToUnixTimeSeconds();
                await _db.UpsertLifetimeParameterAsync("last_refill_epoch_sec", (decimal)epochSec);
            }

            // #7 shots breakdown table — (refill_timestamp, blast_count_since_previous_refill) pairs
            var breakdown = await ComputeShotsBreakdownAsync(epoch, now);
            await _db.ClearLifetimeShotsBreakdownAsync();
            foreach (var (ts, count) in breakdown)
                await _db.InsertLifetimeShotsBreakdownAsync(ts, count);

            // #8 avg_shot_refill_time_sec = total machine-on seconds ÷ COV refill event count
            double machineOnSec = await ComputeMachineOnTimeSecondsAsync(epoch, now);
            int refillCount = (await _db.GetStateChangesAsync(TAG_SHOT_REFILL, epoch, now))
                .Count(r => r.StorageReason == "COV" || r.StorageReason == "VALUE_CHANGE" || r.StorageReason == "STATE_CHANGE");
            decimal avgRefillTimeSec = refillCount > 0
                ? (decimal)Math.Round(machineOnSec / refillCount, 1)
                : 0;
            await _db.UpsertLifetimeParameterAsync("avg_shot_refill_time_sec", avgRefillTimeSec);

            _logger.LogDebug("Lifetime parameters updated at {time}", now);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeLifetimeParametersAsync");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2 — Filtered parameters (called by FilteredCalculationService)
    // Writes aggregate results to plc_filtered_parameters, per-cycle data to
    // plc_filtered_cycle_data, and shots breakdown to plc_filtered_shots_breakdown.
    // ════════════════════════════════════════════════════════════════════════

    public async Task ComputeFilteredParametersAsync(
        int requestId,
        DateTime filterStart, DateTime filterEnd,
        string filterBy,
        int? filterCycleFrom, int? filterCycleTo,
        string? filterMetalName)
    {
        try
        {
            // 1. Resolve which cycles are in scope
            List<PlcCycle> cycles = filterBy switch
            {
                "cycle" when filterCycleFrom.HasValue && filterCycleTo.HasValue
                    => await _db.GetCyclesByNumberRangeAsync(filterCycleFrom.Value, filterCycleTo.Value),
                "metal" when !string.IsNullOrEmpty(filterMetalName)
                    => await _db.GetCyclesByMetalNameAsync(filterMetalName),
                _   => await _db.GetCyclesByTimeRangeAsync(filterStart, filterEnd)
            };

            DateTime windowStart = cycles.Count > 0 ? cycles.Min(c => c.BlastStart) : filterStart;
            DateTime windowEnd   = cycles.Count > 0 ? cycles.Max(c => c.BlastEnd)   : filterEnd;

            // 2. Shared window parameters (#2, #6, #10, reblast_count)
            var windowParams = await ComputeWindowParametersAsync(windowStart, windowEnd);
            foreach (var kv in windowParams)
                await _db.InsertFilteredParameterAsync(requestId, kv.Key, kv.Value);

            // 3. #3 production: last Tonnage value − first Tonnage value in window
            double productionKg = await ComputeProductionKgWindowAsync(windowStart, windowEnd);
            await _db.InsertFilteredParameterAsync(requestId, "production_qty_kg", (decimal)Math.Round(productionKg, 2));

            // 4. #4 energy: per-cycle avg amps × duration hours, cycles in scope only
            double totalKwh = await ComputeEnergyKwhFromCyclesAsync(cycles);
            await _db.InsertFilteredParameterAsync(requestId, "energy_kwh_total", (decimal)Math.Round(totalKwh, 3));

            // 5. #5 energy per casting
            double energyPerCasting = productionKg > 0 ? totalKwh / productionKg : 0;
            await _db.InsertFilteredParameterAsync(requestId, "energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4));

            // 6. #11 last_refill_epoch_sec
            var refillRecords = await _db.GetStateChangesAsync(TAG_SHOT_REFILL, windowStart, windowEnd);
            if (refillRecords.Count > 0)
            {
                long epochSec = ((DateTimeOffset)refillRecords.Last().Timestamp).ToUnixTimeSeconds();
                await _db.InsertFilteredParameterAsync(requestId, "last_refill_epoch_sec", (decimal)epochSec);
            }

            // 7. #7 shots breakdown table (effective_shots_usage and avg_shot_refill_time_sec not in Section 2)
            var breakdown = await ComputeShotsBreakdownAsync(windowStart, windowEnd);
            foreach (var (ts, count) in breakdown)
                await _db.InsertFilteredShotsBreakdownAsync(requestId, ts, count);

            // 8. Per-cycle breakdown
            if (cycles.Count > 0)
                await ComputePerCycleDataAsync(requestId, cycles);

            _logger.LogDebug("Filtered parameters stored for request {id} ({n} cycles)", requestId, cycles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeFilteredParametersAsync for request {id}", requestId);
            throw;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // SHARED WINDOW PARAMETERS — numeric parameters identical for S1 and S2
    // (#2 machine_utility_pct, #6 blast_time_sec, #10 cycle_count, reblast_count)
    // ════════════════════════════════════════════════════════════════════════

    private async Task<Dictionary<string, decimal?>> ComputeWindowParametersAsync(DateTime start, DateTime end)
    {
        var result = new Dictionary<string, decimal?>();

        // Fetch blast records once — reused for both blast_time and cycle_count
        var blastRecords = await _db.GetStateChangesAsync(TAG_BLAST, start, end);

        double blastSec = ComputeOnTimeSeconds(blastRecords, start, end,
            isOn: v => v == "1" || v?.ToLower() == "true");
        result["blast_time_sec"] = (decimal)Math.Round(blastSec, 1);

        // Machine utility denominator: only count machine-on time from when blast data begins
        // in this window.  Without this, the lifetime ratio is meaningless when blast recording
        // started months after machine-status recording (e.g., blast data from today, machine
        // status from January → utility near 0% even when machine blasts constantly).
        DateTime machineStart = blastRecords.Count > 0 ? blastRecords[0].Timestamp : start;
        double machineOnSec = await ComputeMachineOnTimeSecondsAsync(machineStart, end);
        double machineUtility = machineOnSec > 0 ? Math.Min(blastSec / machineOnSec * 100.0, 100.0) : 0;
        result["machine_utility_pct"] = (decimal)Math.Round(machineUtility, 2);

        result["cycle_count"] = (decimal)CountRisingEdges(blastRecords);

        return result;
    }

    // ════════════════════════════════════════════════════════════════════════
    // PER-CYCLE BREAKDOWN (Section 2 only — plc_filtered_cycle_data)
    // ════════════════════════════════════════════════════════════════════════

    private async Task ComputePerCycleDataAsync(int requestId, List<PlcCycle> cycles)
    {
        PlcCycle? prevCycle = await _db.GetCyclePrecedingAsync(cycles[0].CycleNumber);
        double prevTonnage  = prevCycle?.TonnageKg ?? 0;

        foreach (var cycle in cycles)
        {
            // Production: accumulated tonnage delta vs previous cycle
            double production = cycle.TonnageKg.HasValue
                ? Math.Max(cycle.TonnageKg.Value - prevTonnage, 0)
                : 0;

            // Energy: avg amps per impeller × duration hours, summed across all impellers
            double durationHours = cycle.DurationSec / 3600.0;
            double energyKwh = 0;
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                double avgAmps = await ComputeAvgAmpsInWindowAsync(i, cycle.BlastStart, cycle.BlastEnd);
                energyKwh += avgAmps * durationHours;
            }

            // Shots usage: refill weight during cycle / production (kept for per-cycle table)
            double refillInCycle = await ComputeTotalRefillWeightAsync(cycle.BlastStart, cycle.BlastEnd);
            double shotsUsage    = production > 0 ? refillInCycle / production : 0;

            await _db.InsertFilteredCycleDataAsync(
                requestId, cycle,
                (decimal)Math.Round(production, 2),
                (decimal)Math.Round(energyKwh,  3),
                (decimal)Math.Round(shotsUsage,  4));

            prevTonnage = cycle.TonnageKg ?? prevTonnage;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // FORMULA IMPLEMENTATIONS
    // ════════════════════════════════════════════════════════════════════════

    // #3 Production — Section 2: last Tonnage value minus first Tonnage value in window
    private async Task<double> ComputeProductionKgWindowAsync(DateTime start, DateTime end)
    {
        var records = await _db.GetStateChangesAsync(TAG_TONNAGE, start, end);
        if (records.Count < 2) return 0;
        double first = ParseDouble(records.First().Value);
        double last  = ParseDouble(records.Last().Value);
        return Math.Max(last - first, 0);
    }

    // #4 Energy — per-cycle: arithmetic mean of COV amp readings per impeller × duration hours
    private async Task<double> ComputeEnergyKwhFromCyclesAsync(List<PlcCycle> cycles)
    {
        double totalKwh = 0;
        foreach (var cycle in cycles)
        {
            double durationHours = cycle.DurationSec / 3600.0;
            for (int i = 0; i < _activeImpellerCount; i++)
            {
                double avgAmps = await ComputeAvgAmpsInWindowAsync(i, cycle.BlastStart, cycle.BlastEnd);
                totalKwh += avgAmps * durationHours;
            }
        }
        return totalKwh;
    }

    // Arithmetic mean of all COV amp readings for one impeller within a time window.
    // Falls back to the last known reading before the window when there are no in-window records
    // (short cycles often have no COV event if current stayed steady).
    private async Task<double> ComputeAvgAmpsInWindowAsync(int impellerIndex, DateTime start, DateTime end)
    {
        string tagName = TAG_CURRENT[impellerIndex];
        var records = await _db.GetStateChangesAsync(tagName, start, end);
        if (records.Count > 0)
        {
            double sum = 0;
            foreach (var r in records) sum += ParseDouble(r.Value);
            return sum / records.Count;
        }
        var lastBefore = await _db.GetLatestHistoricalBeforeAsync(tagName, start);
        return ParseDouble(lastBefore?.Value);
    }

    // #7 Shots breakdown — for each consecutive pair of actual refill events (COV only, heartbeats excluded),
    // count Blast ON/OFF rising edges between them.
    // Returns (refill_timestamp, blast_count_since_previous_refill) pairs.
    private async Task<List<(DateTime RefillTimestamp, int BlastCount)>> ComputeShotsBreakdownAsync(
        DateTime start, DateTime end)
    {
        var refillRecords = (await _db.GetStateChangesAsync(TAG_SHOT_REFILL, start, end))
            .Where(r => r.StorageReason == "COV" || r.StorageReason == "VALUE_CHANGE" || r.StorageReason == "STATE_CHANGE")
            .ToList();
        var blastRecords = await _db.GetStateChangesAsync(TAG_BLAST, start, end);

        var result = new List<(DateTime RefillTimestamp, int BlastCount)>();
        for (int i = 1; i < refillRecords.Count; i++)
        {
            DateTime prevRefill = refillRecords[i - 1].Timestamp;
            DateTime currRefill = refillRecords[i].Timestamp;
            int blastCount = blastRecords.Count(r =>
                r.Timestamp > prevRefill &&
                r.Timestamp <= currRefill &&
                (r.Value == "1" || r.Value?.ToLower() == "true") &&
                (r.PreviousValue == "0" || r.PreviousValue?.ToLower() == "false" || r.PreviousValue == null));
            result.Add((currRefill, blastCount));
        }

        return result;
    }

    private async Task<double> ComputeMachineOnTimeSecondsAsync(DateTime start, DateTime end)
    {
        var records = await _db.GetStateChangesAsync(TAG_MACHINE_ST, start, end);
        return ComputeOnTimeSeconds(records, start, end, isOn: v => v != null && v != "0");
    }

    private async Task<double> ComputeBlastTimeSecondsAsync(DateTime start, DateTime end)
    {
        var records = await _db.GetStateChangesAsync(TAG_BLAST, start, end);
        return ComputeOnTimeSeconds(records, start, end, isOn: v => v == "1" || v?.ToLower() == "true");
    }

    private async Task<double> ComputeTotalRefillWeightAsync(DateTime start, DateTime end)
    {
        var records = await _db.GetStateChangesAsync(TAG_SHOT_REFILL, start, end);
        double total = 0;
        foreach (var r in records)
        {
            double val = ParseDouble(r.Value);
            if (val > 0) total += val;
        }
        return total;
    }

    // ════════════════════════════════════════════════════════════════════════
    // LOW-LEVEL HELPERS
    // ════════════════════════════════════════════════════════════════════════

    private static double ComputeOnTimeSeconds(
        List<PlcHistoricalData> records,
        DateTime windowStart,
        DateTime windowEnd,
        Func<string?, bool> isOn)
    {
        if (records.Count == 0) return 0;

        double totalSec  = 0;
        bool currentlyOn = isOn(records[0].PreviousValue);
        // If the signal was already ON at the window boundary we don't know when it turned on
        // before our first record — clamp the segment start to the first record timestamp so
        // a distant epoch window doesn't inflate lifetime totals.
        DateTime segStart = currentlyOn ? records[0].Timestamp : windowStart;

        foreach (var r in records)
        {
            bool newState = isOn(r.Value);
            if (currentlyOn && !newState)
                totalSec += (r.Timestamp - segStart).TotalSeconds;
            else if (!currentlyOn && newState)
                segStart = r.Timestamp;
            currentlyOn = newState;
        }

        if (currentlyOn)
            totalSec += (windowEnd - segStart).TotalSeconds;

        return Math.Max(totalSec, 0);
    }

    private static int CountRisingEdges(List<PlcHistoricalData> records)
    {
        int count = 0;
        foreach (var r in records)
        {
            bool prev = r.PreviousValue == "1" || r.PreviousValue?.ToLower() == "true";
            bool curr = r.Value         == "1" || r.Value?.ToLower()         == "true";
            if (!prev && curr) count++;
        }
        return count;
    }

    private static double ParseDouble(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return double.TryParse(s,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out var d) ? d : 0;
    }
}
