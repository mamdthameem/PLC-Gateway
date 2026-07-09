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

    private const string TAG_BLAST       = "Blast ON/OFF";
    private const string TAG_MACHINE_ST  = "Machine status";
    private const string TAG_SHOT_REFILL = "Refil shots weight";
    private const string TAG_TONNAGE     = "Tonnage";
    private const string ADDR_MACHINE_ST = "DB60.DBB0";

    private const int AggBatchSize = 10000;
    private static readonly string[] AggTags = { TAG_BLAST, TAG_MACHINE_ST, TAG_SHOT_REFILL };
    private static readonly HashSet<string> RefillChangeReasons =
        new(StringComparer.Ordinal) { "COV", "VALUE_CHANGE", "STATE_CHANGE" };

    public CalculationService(
        DatabaseService db,
        ILogger<CalculationService> logger,
        IConfiguration configuration)
    {
        _db     = db;
        _logger = logger;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 1 — Lifetime parameters (incremental; called every minute)
    //
    // Each pass folds only Tier 2 rows newer than the stored watermark into running
    // accumulators (plc_aggregation_state), then emits the 9 lifetime parameters plus the
    // shots-breakdown table. Semantically identical to the previous full-replay engine, but
    // it no longer re-reads the whole history (and energy is a running sum of the per-cycle
    // energy_kwh stored at cycle close, not a per-cycle amp re-query).
    // ════════════════════════════════════════════════════════════════════════

    public async Task ComputeLifetimeParametersAsync()
    {
        try
        {
            var state = await _db.GetAggregationStateAsync();

            // 1. Fold new Tier 2 events into the running accumulators.
            while (true)
            {
                var events = await _db.GetNewAggregationEventsAsync(state.LastHistId, AggTags, AggBatchSize);
                if (events.Count == 0) break;

                foreach (var ev in events)
                {
                    switch (ev.ParameterName)
                    {
                        case TAG_BLAST:      FoldBlast(state, ev);         break;
                        case TAG_MACHINE_ST: FoldMachine(state, ev);       break;
                        case TAG_SHOT_REFILL: await FoldRefillAsync(state, ev); break;
                    }
                    state.LastHistId = ev.Id;
                }

                if (events.Count < AggBatchSize) break;
            }

            // 2. Fold new completed cycles into the running energy total.
            var (energyDelta, maxCycle) = await _db.GetCycleEnergyAboveAsync(state.LastCycleNumber);
            state.EnergyTotal     += energyDelta;
            state.LastCycleNumber  = maxCycle;

            await _db.SaveAggregationStateAsync(state);

            // 3. Emit parameters from the accumulators + live Tier 1 values.
            var now = DateTime.Now;

            var machineStatusRec = await _db.GetCurrentValueAsync(ADDR_MACHINE_ST);
            decimal machineStatusVal = machineStatusRec?.Value != null && machineStatusRec.Value != "0" ? 1m : 0m;
            await _db.UpsertLifetimeParameterAsync("machine_status", machineStatusVal);

            var tonnageRec = await _db.GetCurrentValueByNameAsync(TAG_TONNAGE);
            double productionKg = ParseDouble(tonnageRec?.Value);
            await _db.UpsertLifetimeParameterAsync("production_qty_kg", (decimal)Math.Round(productionKg, 2));

            double blastSec = state.BlastClosedSec +
                (state.BlastOn && state.BlastSegStart.HasValue ? (now - state.BlastSegStart.Value).TotalSeconds : 0);
            await _db.UpsertLifetimeParameterAsync("blast_time_sec", (decimal)Math.Round(blastSec, 1));

            double machineOnSec = state.MachineClosedSec +
                (state.MachineOn && state.MachineSegStart.HasValue ? (now - state.MachineSegStart.Value).TotalSeconds : 0);
            double machineUtility = machineOnSec > 0 ? Math.Min(blastSec / machineOnSec * 100.0, 100.0) : 0;
            await _db.UpsertLifetimeParameterAsync("machine_utility_pct", (decimal)Math.Round(machineUtility, 2));

            await _db.UpsertLifetimeParameterAsync("cycle_count", state.CycleCount);

            double totalKwh = (double)state.EnergyTotal;
            await _db.UpsertLifetimeParameterAsync("energy_kwh_total", (decimal)Math.Round(totalKwh, 3));

            double energyPerCasting = productionKg > 0 ? totalKwh / productionKg : 0;
            await _db.UpsertLifetimeParameterAsync("energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4));

            if (state.LastRefillAnyTs.HasValue)
            {
                long epochSec = ((DateTimeOffset)state.LastRefillAnyTs.Value).ToUnixTimeSeconds();
                await _db.UpsertLifetimeParameterAsync("last_refill_epoch_sec", (decimal)epochSec);
            }

            decimal avgRefillTimeSec = 0;
            if (state.RefillCount > 0 && state.FirstRefillChangeTs.HasValue)
            {
                double elapsedSec = (now - state.FirstRefillChangeTs.Value).TotalSeconds;
                avgRefillTimeSec = (decimal)Math.Round(elapsedSec / state.RefillCount, 1);
            }
            await _db.UpsertLifetimeParameterAsync("avg_shot_refill_time_sec", avgRefillTimeSec);

            _logger.LogDebug("Lifetime parameters updated at {time} (watermark id {id}).", now, state.LastHistId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeLifetimeParametersAsync");
        }
    }

    // Folds one Blast ON/OFF event into blast on-time + cycle-count accumulators.
    // On-time uses a running "currently on" segment seeded from the first record's previous
    // value; cycle_count counts rising edges (off→on) using each record's own previous value.
    private static void FoldBlast(AggregationState s, AggEvent ev)
    {
        bool newState = ev.ValueBool ?? false;

        if (!s.BlastSeeded)
        {
            bool prevOn = IsBlastOn(ev.PreviousValue);
            s.BlastOn = prevOn;
            s.BlastSegStart = prevOn ? ev.Timestamp : null;
            s.BlastSeeded = true;
            s.FirstBlastTs ??= ev.Timestamp;
        }

        if (s.BlastOn && !newState && s.BlastSegStart.HasValue)
            s.BlastClosedSec += (ev.Timestamp - s.BlastSegStart.Value).TotalSeconds;
        else if (!s.BlastOn && newState)
            s.BlastSegStart = ev.Timestamp;
        s.BlastOn = newState;

        if (!IsBlastOn(ev.PreviousValue) && newState)
            s.CycleCount++;
    }

    // Folds one Machine status event into machine on-time. Machine on-time is only accumulated
    // from the first blast timestamp onward (the utility-ratio denominator clamp), matching the
    // previous engine exactly.
    private static void FoldMachine(AggregationState s, AggEvent ev)
    {
        if (s.FirstBlastTs == null) return;
        if (ev.Timestamp <= s.FirstBlastTs.Value) return;

        bool newState = ev.ValueBool ?? false;

        if (!s.MachineSeeded)
        {
            bool prevOn = IsMachineOn(ev.PreviousValue);
            s.MachineOn = prevOn;
            s.MachineSegStart = prevOn ? ev.Timestamp : s.FirstBlastTs;
            s.MachineSeeded = true;
        }

        if (s.MachineOn && !newState && s.MachineSegStart.HasValue)
            s.MachineClosedSec += (ev.Timestamp - s.MachineSegStart.Value).TotalSeconds;
        else if (!s.MachineOn && newState)
            s.MachineSegStart = ev.Timestamp;
        s.MachineOn = newState;
    }

    // Folds one refill-weight event: tracks the latest refill (any reason) for last_refill_epoch,
    // and for actual change events maintains the refill count/first-time and appends a shots-
    // breakdown row (blast rising edges since the previous refill).
    private async Task FoldRefillAsync(AggregationState s, AggEvent ev)
    {
        if (s.LastRefillAnyTs == null || ev.Timestamp > s.LastRefillAnyTs.Value)
            s.LastRefillAnyTs = ev.Timestamp;

        if (!RefillChangeReasons.Contains(ev.StorageReason)) return;

        s.RefillCount++;
        s.FirstRefillChangeTs ??= ev.Timestamp;

        if (s.PrevRefillChangeTs.HasValue)
        {
            int blastCount = await _db.CountBlastRisingEdgesBetweenAsync(
                TAG_BLAST, s.PrevRefillChangeTs.Value, ev.Timestamp);
            await _db.InsertLifetimeShotsBreakdownAsync(ev.Timestamp, blastCount);
        }

        s.PrevRefillChangeTs = ev.Timestamp;
    }

    // ════════════════════════════════════════════════════════════════════════
    // SECTION 2 — Filtered parameters (on-demand, per calculation_requests row)
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

            // Shared window parameters (blast_time_sec, machine_utility_pct, cycle_count).
            var windowParams = await ComputeWindowParametersAsync(windowStart, windowEnd);
            foreach (var kv in windowParams)
                await _db.InsertFilteredParameterAsync(requestId, kv.Key, kv.Value);

            // Production + energy come straight from the values stored per cycle at close.
            double productionKg = cycles.Sum(c => c.ProductionKg ?? 0);
            double totalKwh     = cycles.Sum(c => c.EnergyKwh ?? 0);
            await _db.InsertFilteredParameterAsync(requestId, "energy_kwh_total", (decimal)Math.Round(totalKwh, 3));

            double energyPerCasting = productionKg > 0 ? totalKwh / productionKg : 0;
            await _db.InsertFilteredParameterAsync(requestId, "energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4));

            // Shots breakdown table for the window.
            var breakdown = await ComputeShotsBreakdownAsync(windowStart, windowEnd);
            foreach (var (ts, count) in breakdown)
                await _db.InsertFilteredShotsBreakdownAsync(requestId, ts, count);

            // Per-cycle breakdown + per-metal production split.
            if (cycles.Count > 0)
            {
                await ComputePerCycleDataAsync(requestId, cycles);
                await ComputeMetalProductionAsync(requestId, cycles);
            }

            _logger.LogDebug("Filtered parameters stored for request {id} ({n} cycles)", requestId, cycles.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeFilteredParametersAsync for request {id}", requestId);
            throw;
        }
    }

    // Shared window parameters, identical for the filtered window: replays the (bounded) event
    // stream for the window — acceptable here because Section 2 is user-triggered and windowed.
    private async Task<Dictionary<string, decimal?>> ComputeWindowParametersAsync(DateTime start, DateTime end)
    {
        var result = new Dictionary<string, decimal?>();

        var blastRecords = await _db.GetStateChangesAsync(TAG_BLAST, start, end);

        double blastSec = ComputeOnTimeSeconds(blastRecords, start, end,
            isOn: v => v == "1" || v?.ToLower() == "true");
        result["blast_time_sec"] = (decimal)Math.Round(blastSec, 1);

        DateTime machineStart = blastRecords.Count > 0 ? blastRecords[0].Timestamp : start;
        double machineOnSec = await ComputeMachineOnTimeSecondsAsync(machineStart, end);
        double machineUtility = machineOnSec > 0 ? Math.Min(blastSec / machineOnSec * 100.0, 100.0) : 0;
        result["machine_utility_pct"] = (decimal)Math.Round(machineUtility, 2);

        result["cycle_count"] = (decimal)CountRisingEdges(blastRecords);

        return result;
    }

    // Per-cycle breakdown (plc_filtered_cycle_data): production_kg and energy_kwh are read
    // from the cycle rows; shots_usage = refill weight consumed in the cycle ÷ production.
    private async Task ComputePerCycleDataAsync(int requestId, List<PlcCycle> cycles)
    {
        foreach (var cycle in cycles)
        {
            double production = cycle.ProductionKg ?? 0;
            double energyKwh  = cycle.EnergyKwh ?? 0;

            double refillInCycle = await ComputeTotalRefillWeightAsync(cycle.BlastStart, cycle.BlastEnd);
            double shotsUsage    = production > 0 ? refillInCycle / production : 0;

            await _db.InsertFilteredCycleDataAsync(
                requestId, cycle,
                (decimal)Math.Round(production, 2),
                (decimal)Math.Round(energyKwh,  3),
                (decimal)Math.Round(shotsUsage, 4));
        }
    }

    // Per-metal production: each cycle's production is split across its declared casting metals
    // proportionally by declared weight. Cycles with no declared weight go to 'unspecified'.
    private async Task ComputeMetalProductionAsync(int requestId, List<PlcCycle> cycles)
    {
        var byMetal = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var cycle in cycles)
        {
            double production = cycle.ProductionKg ?? 0;
            if (production <= 0) continue;

            var slots = new (string? Name, double Weight)[]
            {
                (cycle.Metal1Name, cycle.Metal1WeightKg ?? 0),
                (cycle.Metal2Name, cycle.Metal2WeightKg ?? 0),
                (cycle.Metal3Name, cycle.Metal3WeightKg ?? 0),
                (cycle.Metal4Name, cycle.Metal4WeightKg ?? 0),
            };

            double totalWeight = slots.Where(s => s.Weight > 0).Sum(s => s.Weight);

            if (totalWeight <= 0)
            {
                Accumulate(byMetal, "unspecified", production);
                continue;
            }

            foreach (var slot in slots)
            {
                if (slot.Weight <= 0) continue;
                string name = string.IsNullOrWhiteSpace(slot.Name) ? "unspecified" : slot.Name!;
                Accumulate(byMetal, name, production * slot.Weight / totalWeight);
            }
        }

        foreach (var kv in byMetal)
            await _db.InsertFilteredMetalProductionAsync(requestId, kv.Key, (decimal)Math.Round(kv.Value, 2));
    }

    private static void Accumulate(Dictionary<string, double> map, string key, double value)
    {
        map[key] = (map.TryGetValue(key, out var cur) ? cur : 0) + value;
    }

    // #7 shots breakdown — for each consecutive pair of actual refill events, count blast
    // rising edges between them. Returns (refill_timestamp, blast_count) pairs.
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

    private static bool IsBlastOn(string? v) => v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
    private static bool IsMachineOn(string? v) => !string.IsNullOrEmpty(v) && v != "0";

    private static double ComputeOnTimeSeconds(
        List<PlcHistoricalData> records,
        DateTime windowStart,
        DateTime windowEnd,
        Func<string?, bool> isOn)
    {
        if (records.Count == 0) return 0;

        double totalSec  = 0;
        bool currentlyOn = isOn(records[0].PreviousValue);
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
