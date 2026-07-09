// Auth & user management
export type UserRole = 'admin' | 'user';

export interface User {
  id: string;
  email: string;
  name: string;
  username?: string;
  industryName?: string;
  state?: string;
  password?: string;
  role: UserRole;
  customerId?: string;
  isApproved: boolean;
  unapprovedReason?: string;
  validUntil?: Date;
  assignedMachineIds: string[];
  createdAt: Date;
}

export interface Customer {
  id: string;
  name: string;
  email: string;
  companyName?: string;
  address?: string;
  phone?: string;
  createdAt: Date;
  machineIds: string[];
  userIds: string[];
}

// Live machine status (plc_current_values WHERE address = 'DB60.DBB0')
export interface MachineStatus {
  value: string;
  lastUpdated: string;
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
