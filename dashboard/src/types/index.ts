// Authenticated dashboard user (from the backend JWT). Single-site only — no tenant,
// customer, or subscription concept exists in this client app.
export type UserRole = 'admin' | 'user';

export interface User {
  id: string;
  username: string;
  email: string;
  name: string;
  role: UserRole;
}

// Live machine status (plc_current_values WHERE address = 'DB60.DBB0') joined with the
// gateway's PLC link state. While the PLC is disconnected the backend forces `value` to '0'
// (machine treated as OFF) and flags the row stale, so the tile reports "Stopped" and can say
// why.
export interface MachineStatus {
  value: string;
  lastUpdated: string;
  isStale: boolean;
  plcConnected: boolean;
  lastScanAt: string | null;
}

// Section 1 — plc_lifetime_parameters
export interface LifetimeParameter {
  parameterName: string;
  value: string;
  updatedAt: string;
}

// Section 1 — plc_shots_breakdown
export interface ShotsBreakdownEntry {
  refillTimestamp: string;
  blastCount: number;
}

// Section 1 — plc_current_values (amps)
export interface AmpReading {
  parameterName: string;
  value: string;
  lastUpdated: string;
}

// Section 1 — plc_spare_status
export interface SpareStatus {
  impellerNum: number;
  spareIndex: number;
  spareName: string;
  thresholdHours: number;
  currentRunHours: number;
  triggerActive: boolean;
  lastReplacedAt: string | null;
  lastUpdatedAt: string;
}

// Historical time-series (plc_historical_data)
export interface HistoricalPoint {
  value: string;
  timestamp: string;
}

// One pre-aggregated trend bucket (plc_daily_trends, via /api/trends). Backs the all-time
// Section 1 graphs. Every derived figure is computed server-side — the dashboard plots these
// values as delivered and never recomputes them.
export interface DailyTrend {
  day: string;
  machineOnSec: number;
  blastOnSec: number;
  utilityPct: number;
  cycleCount: number;
  productionKg: number;
  tonnageEnd: number | null;
  energyKwh: number;
  efficiencyKwhPerKg: number;
}

// 'hour' is computed live from Tier 2 over a bounded window (Section 2 short filters);
// 'day'/'month' are served from the plc_daily_trends rollup and are safe for all-time ranges.
export type TrendBucket = 'hour' | 'day' | 'month';

// Latest blast cycle (plc_cycles)
export interface LatestCycle {
  blastStart: string;
  blastEnd: string;
}

// Section 2 — calculation_requests + plc_filtered_*
//
// NOTE ON WORDING: the wire format still says "metal" (filterBy: 'metal', filterMetalName,
// metalName, metal1Name…) because the PLC tag names, the DB columns and the admin API contract
// all say metal. Only what the USER SEES says "Item". See README "Casting item vs casting metal".
export interface FilterRequest {
  filterStart: string;
  filterEnd: string;
  periodLabel?: string | null;
  filterBy: 'time' | 'cycle' | 'metal';
  filterCycleFrom?: number | null;
  filterCycleTo?: number | null;
  filterMetalName?: string | null;
  /** Section 2 parameter keys to compute. Omitted/empty means all of them. */
  selectedParameters?: Section2ParamKey[];
}

/**
 * Every Section 2 parameter the filter can compute — the toggle list, in display order.
 *
 * Section 1-only parameters are deliberately absent: machine status, effective shots usage, last
 * refill time, average shot refill interval, and the shots-breakdown and spare-health tables do
 * not respond to any filter and are rendered above the filter bar.
 *
 * Must stay in sync with CalculationService.Section2ParameterKeys — the backend rejects unknown
 * keys with a 400 rather than silently ignoring them, so a drift here fails loudly.
 */
export const SECTION2_PARAM_KEYS = [
  'machine_utility_pct',
  'production_qty_kg',
  'energy_kwh_total',
  'energy_per_casting_kwh_kg',
  'blast_time_sec',
  'cycle_count',
  'impeller_current',
] as const;

export type Section2ParamKey = typeof SECTION2_PARAM_KEYS[number];

/**
 * Section 1 parameters with NO Section 2 counterpart — rendered ABOVE the filter bar.
 *
 * Each is unfilterable by nature, not by omission: machine_status is a live state rather than a
 * window aggregate; the two refill figures and effective_shots_usage_kg_per_ton are cumulative
 * since commissioning by definition. Nothing below the filter bar ever shows these.
 *
 * machine_status is in the list for completeness but is rendered by MachineStatusTile above the
 * grid (it also carries the PLC link state), so the grid filters it out.
 */
export const SECTION1_ONLY_PARAM_KEYS: readonly string[] = [
  'machine_status',
  'avg_shot_refill_time_sec',
  'last_refill_epoch_sec',
  'effective_shots_usage_kg_per_ton',
];

/**
 * Parameters that exist in BOTH sections — rendered BELOW the filter bar.
 *
 * With no filter applied these tiles show the Section 1 all-time values, live. Applying a filter
 * replaces them with the Section 2 values for the chosen scope. Same parameters, same position on
 * the page, different scope — which is the whole point of the split.
 *
 * Mirrors SECTION2_PARAM_KEYS minus `impeller_current`, which is a panel rather than a scalar tile
 * and is rendered alongside this grid in both states (live amps / filtered averages).
 */
export const SHARED_PARAM_KEYS: readonly string[] = [
  'machine_utility_pct',
  'production_qty_kg',
  'energy_kwh_total',
  'energy_per_casting_kwh_kg',
  'blast_time_sec',
  'cycle_count',
];

/**
 * Parameters that cannot be attributed to a single casting item, and are therefore disabled while
 * an item filter is active. machine_utility_pct's denominator is MACHINE on-time — the machine
 * powered up, blasting anything or idle — which no item owns a share of.
 */
export const ITEM_FILTER_UNSUPPORTED: readonly Section2ParamKey[] = ['machine_utility_pct'];

export interface FilterStatus {
  status: 'pending' | 'processing' | 'done' | 'error';
  processedAt?: string | null;
}

export interface FilterResult {
  parameterName: string;
  value: string;
}

// Section 2 — plc_filtered_metal_production. productionKg is the sum of DECLARED casting-metal
// weights for that metal over the filtered scope, not a share of the Tonnage accumulator.
export interface FilteredMetalProduction {
  metalName: string;
  productionKg: number;
}

export interface FilteredCycle {
  cycleNumber: number;
  blastStart: string;
  blastEnd: string;
  metal1Name: string | null;
  metal1WeightKg: number | null;
  metal2Name: string | null;
  metal2WeightKg: number | null;
  metal3Name: string | null;
  metal3WeightKg: number | null;
  metal4Name: string | null;
  metal4WeightKg: number | null;
  productionKg: number;
  energyKwh: number;
}

// Section 2 — plc_filtered_amps_data. Mirrors the Section 1 Amps tile/graph, scoped to the
// filter's cycles instead of "last completed cycle". overallAvgAmps is duration-weighted across
// cycles (a short cycle counts less than a long one); cycles.avgAmps is null where the cycle had
// no in-window sample for that impeller, rendered as a gap rather than a false zero.
export interface FilteredAmpsCyclePoint {
  cycleNumber: number;
  blastEnd: string;
  avgAmps: number | null;
}

export interface FilteredAmps {
  impellerNumber: number;
  overallAvgAmps: number | null;
  cycles: FilteredAmpsCyclePoint[];
}

export type PeriodLabel = 'hour' | 'shift' | 'day' | 'week' | 'month' | 'year';
