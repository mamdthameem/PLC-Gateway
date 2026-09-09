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

            // Shot consumed per tonne of casting: total_refill_weight_kg ÷ (production_qty_kg / 1000).
            // Both cumulative since commissioning. Lower is better — it is a consumption rate, not
            // an efficiency ratio (this is the inverse of the old kg/kg form, rescaled to tonnes).
            //
            // The guard is on the DENOMINATOR, production: null (not zero) when nothing has been
            // cast yet, so the dashboard shows "—" instead of a misleading 0 or an infinity.
            decimal? shotsPerTonne = productionKg > 0
                ? Math.Round(state.TotalRefillWeightKg / (decimal)(productionKg / 1000.0), 4)
                : null;
            await _db.UpsertLifetimeParameterAsync("effective_shots_usage_kg_per_ton", shotsPerTonne);

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

        // effective_shots_usage_kg_per_ton NUMERATOR: sum only real change-event weights, guarding
        // against the occasional zero/negative glitch reading. Unchanged by the kg/T conversion —
        // that inverted the ratio, it did not touch how refill weight accumulates.
        if (ev.ValueNum is > 0) s.TotalRefillWeightKg += (decimal)ev.ValueNum.Value;

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

    // The complete set of Section 2 parameter keys a request may ask for. This is the ONLY list —
    // the dashboard's toggles, the admin API's validation and the compute gating all read it, so
    // there is no second place for it to drift.
    //
    // Section 1-only parameters are deliberately absent: machine_status, avg_shot_refill_time_sec,
    // last_refill_epoch_sec, effective_shots_usage_kg_per_ton and the shots-breakdown /
    // spare-health tables do not respond to any filter and live above the filter bar.
    //
    // "impeller_current" is a pseudo-parameter: it is not a plc_filtered_parameters row but the
    // plc_filtered_amps_data panel. It is in this list because it is a user-facing Section 2
    // output that the toggles switch on and off like any other.
    public static readonly string[] Section2ParameterKeys =
    {
        "machine_utility_pct",
        "production_qty_kg",
        "energy_kwh_total",
        "energy_per_casting_kwh_kg",
        "blast_time_sec",
        "cycle_count",
        "impeller_current",
    };

    // machine_utility_pct is blast on-time ÷ MACHINE on-time. Machine on-time is the machine being
    // powered up — blasting anything, or idle between jobs. None of that is attributable to one
    // casting item, so under an item filter the honest answer is "not applicable" rather than a
    // number computed over a window that necessarily includes other items' cycles.
    private static readonly HashSet<string> ItemFilterUnsupported =
        new(StringComparer.Ordinal) { "machine_utility_pct" };

    /// <summary>
    /// Resolves the requested parameter keys into the set actually computed.
    /// Null/empty means "everything" — that is what pre-toggle rows and callers that omit the
    /// field mean, and it keeps the admin API backward compatible. Unknown keys are dropped, and
    /// under an item filter the parameters that cannot be attributed to a single item are dropped
    /// too (the dashboard disables those toggles, but the backend must not rely on that).
    /// </summary>
    public static HashSet<string> ResolveSelection(string[]? requested, bool isItemFilter)
    {
        var selected = requested is { Length: > 0 }
            ? new HashSet<string>(requested.Intersect(Section2ParameterKeys, StringComparer.Ordinal), StringComparer.Ordinal)
            : new HashSet<string>(Section2ParameterKeys, StringComparer.Ordinal);

        if (isItemFilter) selected.ExceptWith(ItemFilterUnsupported);
        return selected;
    }

    public async Task ComputeFilteredParametersAsync(
        int requestId,
        DateTime filterStart, DateTime filterEnd,
        string filterBy,
        int? filterCycleFrom, int? filterCycleTo,
        string? filterMetalName,
        string[]? selectedParameters = null)
    {
        try
        {
            // An item filter is the only mode whose scope is not a contiguous stretch of time, so
            // several parameters below take a different (cycle-derived) path for it.
            bool isItemFilter = filterBy == "metal" && !string.IsNullOrWhiteSpace(filterMetalName);

            var want = ResolveSelection(selectedParameters, isItemFilter);
            if (want.Count == 0)
            {
                _logger.LogWarning("Request {id} selected no computable parameters — nothing to do.", requestId);
                return;
            }

            // The cycle set is the scope definition for everything else, so it is always read.
            // For an item filter the predicate is applied here, in SQL — every parameter below is
            // then derived from these cycles alone, never from a time window spanning them.
            List<PlcCycle> cycles = filterBy switch
            {
                "cycle" when filterCycleFrom.HasValue && filterCycleTo.HasValue
                    => await _db.GetCyclesByNumberRangeAsync(filterCycleFrom.Value, filterCycleTo.Value),
                "metal" when !string.IsNullOrEmpty(filterMetalName)
                    => await _db.GetCyclesByMetalNameAsync(filterMetalName),
                _   => await _db.GetCyclesByTimeRangeAsync(filterStart, filterEnd)
            };

            // ── Declared casting-item weights ────────────────────────────────────────────────
            // Backs BOTH production_qty_kg and the energy_per_casting denominator, so it is built
            // when either is wanted. Under an item filter only the filtered item's slots count
            // (a cycle may declare up to 4 different items; charging this item's kWh/kg against
            // another item's weight would understate it).
            bool needItemWeights = want.Contains("production_qty_kg") || want.Contains("energy_per_casting_kwh_kg");
            var itemTotals = needItemWeights
                ? SumDeclaredItemWeights(cycles, isItemFilter ? filterMetalName : null)
                : new Dictionary<string, double>(StringComparer.Ordinal);
            double declaredKg = itemTotals.Values.Sum();

            if (want.Contains("production_qty_kg"))
            {
                // Section 2 production is the sum of DECLARED casting-item weights, not the
                // Tonnage accumulator (that is Section 1's, and the two deliberately differ).
                await _db.InsertFilteredParameterAsync(
                    requestId, "production_qty_kg", (decimal)Math.Round(declaredKg, 2));

                foreach (var kv in itemTotals)
                    await _db.InsertFilteredMetalProductionAsync(requestId, kv.Key, (decimal)Math.Round(kv.Value, 2));
            }

            // ── Energy ──────────────────────────────────────────────────────────────────────
            if (want.Contains("energy_kwh_total") || want.Contains("energy_per_casting_kwh_kg"))
            {
                // Straight from the value stored per cycle at close — no amp re-query.
                double totalKwh = cycles.Sum(c => c.EnergyKwh ?? 0);

                if (want.Contains("energy_kwh_total"))
                    await _db.InsertFilteredParameterAsync(
                        requestId, "energy_kwh_total", (decimal)Math.Round(totalKwh, 3));

                if (want.Contains("energy_per_casting_kwh_kg"))
                {
                    double energyPerCasting = declaredKg > 0 ? totalKwh / declaredKg : 0;
                    await _db.InsertFilteredParameterAsync(
                        requestId, "energy_per_casting_kwh_kg", (decimal)Math.Round(energyPerCasting, 4));
                }
            }

            // ── Blast time / utility / cycle count ──────────────────────────────────────────
            if (isItemFilter)
            {
                // Cycle-derived: the matching cycles need not be contiguous in time, so replaying
                // an event window between the first and last of them would sweep in every other
                // item's cycles that happen to fall in between.
                if (want.Contains("blast_time_sec"))
                {
                    double blastSec = cycles.Sum(c => Math.Max((c.BlastEnd - c.BlastStart).TotalSeconds, 0));
                    await _db.InsertFilteredParameterAsync(
                        requestId, "blast_time_sec", (decimal)Math.Round(blastSec, 1));
                }

                if (want.Contains("cycle_count"))
                    await _db.InsertFilteredParameterAsync(requestId, "cycle_count", cycles.Count);
            }
            else
            {
                // Time and cycle filters DO describe a contiguous span, so the event replay stays —
                // it correctly counts blast seconds at the window edges that no completed cycle row
                // covers. Each query below is skipped when nothing selected needs it.
                DateTime windowStart = cycles.Count > 0 ? cycles.Min(c => c.BlastStart) : filterStart;
                DateTime windowEnd   = cycles.Count > 0 ? cycles.Max(c => c.BlastEnd)   : filterEnd;

                var windowParams = await ComputeWindowParametersAsync(windowStart, windowEnd, want);
                foreach (var kv in windowParams)
                    await _db.InsertFilteredParameterAsync(requestId, kv.Key, kv.Value);
            }

            // ── Per-cycle rows ──────────────────────────────────────────────────────────────
            // plc_filtered_cycle_data backs the four per-cycle graphs, the Excel "Cycles" sheet and
            // the JOIN behind the amps panel, so it is written when any of those is in scope. If
            // only machine_utility_pct / production_qty_kg were selected it is skipped entirely.
            bool needCycleRows =
                want.Contains("energy_kwh_total")          || want.Contains("energy_per_casting_kwh_kg") ||
                want.Contains("blast_time_sec")            || want.Contains("cycle_count")               ||
                want.Contains("impeller_current");

            if (cycles.Count > 0)
            {
                if (needCycleRows)
                    await ComputePerCycleDataAsync(requestId, cycles);

                // The single most expensive Section 2 query (an AVG over dense 1 Hz current rows),
                // so the toggle saving the most work is this one.
                if (want.Contains("impeller_current"))
                    await _db.InsertFilteredAmpsDataAsync(requestId, cycles);
            }

            _logger.LogDebug(
                "Filtered parameters stored for request {id} ({n} cycles, {k} of {total} parameters)",
                requestId, cycles.Count, want.Count, Section2ParameterKeys.Length);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in ComputeFilteredParametersAsync for request {id}", requestId);
            throw;
        }
    }

    // Shared window parameters for time and cycle filters: replays the (bounded) event stream for
    // the window — acceptable here because Section 2 is user-triggered and windowed.
    //
    // Only the queries the selection actually needs are issued. The Blast ON/OFF read is shared by
    // all three parameters (it is the utility numerator as well as its own), so it runs when any
    // one of them is wanted; the separate Machine status read runs only for machine_utility_pct.
    private async Task<Dictionary<string, decimal?>> ComputeWindowParametersAsync(
        DateTime start, DateTime end, HashSet<string> want)
    {
        var result = new Dictionary<string, decimal?>();

        bool needBlast = want.Contains("blast_time_sec")
                      || want.Contains("cycle_count")
                      || want.Contains("machine_utility_pct");
        if (!needBlast) return result;

        var blastRecords = await _db.GetStateChangesAsync(TAG_BLAST, start, end);

        double blastSec = ComputeOnTimeSeconds(blastRecords, start, end,
            isOn: v => v == "1" || v?.ToLower() == "true");

        if (want.Contains("blast_time_sec"))
            result["blast_time_sec"] = (decimal)Math.Round(blastSec, 1);

        if (want.Contains("machine_utility_pct"))
        {
            DateTime machineStart = blastRecords.Count > 0 ? blastRecords[0].Timestamp : start;
            double machineOnSec = await ComputeMachineOnTimeSecondsAsync(machineStart, end);
            double machineUtility = machineOnSec > 0 ? Math.Min(blastSec / machineOnSec * 100.0, 100.0) : 0;
            result["machine_utility_pct"] = (decimal)Math.Round(machineUtility, 2);
        }

        if (want.Contains("cycle_count"))
            result["cycle_count"] = (decimal)CountRisingEdges(blastRecords);

        return result;
    }

    // Per-cycle breakdown (plc_filtered_cycle_data): production_kg and energy_kwh are read
    // straight from the cycle rows. Batched into one round trip regardless of cycle count.
    private async Task ComputePerCycleDataAsync(int requestId, List<PlcCycle> cycles)
    {
        var rows = cycles
            .Select(cycle => (
                Cycle: cycle,
                ProductionKg: (decimal)Math.Round(cycle.ProductionKg ?? 0, 2),
                EnergyKwh: (decimal)Math.Round(cycle.EnergyKwh ?? 0, 3)))
            .ToList();

        await _db.InsertFilteredCycleDataBatchAsync(requestId, rows);
    }

    // Section 2 production per casting item: the SUM OF DECLARED WEIGHTS for each item name
    // across the in-scope cycles. This is what the plant declared it cast, grouped by item —
    // it is not derived from the Tonnage accumulator at all.
    //
    // (The DB columns and PLC tags still say "metal" — only the dashboard's wording changed to
    // "item". See README "Casting item vs casting metal".)
    //
    // Consequence worth knowing: this total need not equal the measured Tonnage delta for the
    // same window (declared vs actual). Section 1's production_qty_kg remains the raw Tonnage
    // accumulator, so the two sections answer deliberately different questions.
    //
    // A slot only contributes when it carries a weight > 0. A weight declared with a blank name
    // is attributed to 'unspecified'; a cycle that declared nothing contributes nothing rather
    // than inventing a figure for it.
    //
    // onlyItem scopes the sum to a single declared item. A cycle can declare up to 4 items, so a
    // cycle selected because it contains "Aluminium" may also carry "Iron" — under an item filter
    // that other weight must not land in this item's production or in its kWh/kg denominator.
    private static Dictionary<string, double> SumDeclaredItemWeights(List<PlcCycle> cycles, string? onlyItem = null)
    {
        var byItem = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (var cycle in cycles)
        {
            var slots = new (string? Name, double Weight)[]
            {
                (cycle.Metal1Name, cycle.Metal1WeightKg ?? 0),
                (cycle.Metal2Name, cycle.Metal2WeightKg ?? 0),
                (cycle.Metal3Name, cycle.Metal3WeightKg ?? 0),
                (cycle.Metal4Name, cycle.Metal4WeightKg ?? 0),
            };

            foreach (var slot in slots)
            {
                if (slot.Weight <= 0) continue;
                string name = string.IsNullOrWhiteSpace(slot.Name) ? "unspecified" : slot.Name!;

                // GetCyclesByMetalNameAsync matches the item name exactly, so match it the same
                // way here — otherwise the scoped sum and the cycle set could disagree.
                if (onlyItem != null && !string.Equals(name, onlyItem, StringComparison.Ordinal)) continue;

                Accumulate(byItem, name, slot.Weight);
            }
        }

        return byItem;
    }

    private static void Accumulate(Dictionary<string, double> map, string key, double value)
    {
        map[key] = (map.TryGetValue(key, out var cur) ? cur : 0) + value;
    }

    // The Section 2 shots breakdown was removed: shot refills are a machine-level, Section 1 fact
    // that does not respond to a filter (a refill interval spans whatever cycles happen to fall in
    // it, mixing items), so the "Blast Cycles per Refill Interval" chart now lives only in Section 1
    // above the filter bar. Section 1's incremental FoldRefillAsync above is untouched.

    private async Task<double> ComputeMachineOnTimeSecondsAsync(DateTime start, DateTime end)
    {
        var records = await _db.GetStateChangesAsync(TAG_MACHINE_ST, start, end);
        return ComputeOnTimeSeconds(records, start, end, isOn: v => v != null && v != "0");
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
