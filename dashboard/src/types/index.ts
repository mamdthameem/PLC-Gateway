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
export interface FilterRequest {
  filterStart: string;
  filterEnd: string;
  periodLabel?: string | null;
  filterBy: 'time' | 'cycle' | 'metal';
  filterCycleFrom?: number | null;
  filterCycleTo?: number | null;
  filterMetalName?: string | null;
}

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
  shotsUsage: number;
}

export type PeriodLabel = 'hour' | 'shift' | 'day' | 'week' | 'month' | 'year';
