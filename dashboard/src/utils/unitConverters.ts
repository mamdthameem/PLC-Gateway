export function secondsToHoursMin(sec: number): string {
  if (!isFinite(sec) || sec < 0) return '—';
  const d = Math.floor(sec / 86400);
  const h = Math.floor((sec % 86400) / 3600);
  const m = Math.floor((sec % 3600) / 60);
  if (d > 0) return `${d}d ${h}h ${m}m`;
  if (h === 0) return `${m} min`;
  return `${h}h ${m}m`;
}

export function secondsToMinutes(sec: number): string {
  if (!isFinite(sec) || sec < 0) return '—';
  return `${(sec / 60).toFixed(1)} min`;
}

export function epochToLocalDatetime(epoch: number): string {
  if (!isFinite(epoch) || epoch === 0) return '—';
  return new Date(epoch * 1000).toLocaleString();
}

export function formatPercent(val: number): string {
  if (!isFinite(val)) return '—';
  return `${val.toFixed(2)} %`;
}

export function formatKwh(val: number): string {
  if (!isFinite(val)) return '—';
  return `${val.toLocaleString(undefined, { minimumFractionDigits: 3, maximumFractionDigits: 3 })} kWh`;
}

export function formatKwhPerKg(val: number): string {
  if (!isFinite(val)) return '—';
  return `${val.toFixed(4)} kWh/kg`;
}

export function formatKg(val: number): string {
  if (!isFinite(val)) return '—';
  return `${val.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} kg`;
}

export function formatHours(val: number): string {
  if (!isFinite(val) || val < 0) return '—';
  const h = Math.floor(val);
  const m = Math.round((val - h) * 60);
  if (h === 0) return `${m} min`;
  return `${h}h ${m}m`;
}

export function formatRunHours(val: number): string {
  if (!isFinite(val) || val < 0) return '—';
  return `${val.toLocaleString(undefined, { minimumFractionDigits: 1, maximumFractionDigits: 1 })} hrs`;
}

export const PARAM_META: Record<string, { label: string; unit?: string }> = {
  machine_utility_pct:       { label: 'Machine Utility',      unit: '%' },
  production_qty_kg:         { label: 'Production' },
  energy_kwh_total:          { label: 'Total Energy' },
  energy_per_casting_kwh_kg: { label: 'Energy per Casting' },
  blast_time_sec:            { label: 'Blast Time' },
  cycle_count:               { label: 'Blast Cycles',         unit: 'cycles' },
  avg_shot_refill_time_sec:  { label: 'Avg Shot Refill Time' },
  last_refill_epoch_sec:     { label: 'Last Shot Refill' },
};

export function formatParameterValue(name: string, raw: string): string {
  const n = parseFloat(raw);
  switch (name) {
    case 'machine_utility_pct':
      return formatPercent(n);
    case 'production_qty_kg':
      return formatKg(n);
    case 'energy_kwh_total':
      return formatKwh(n);
    case 'energy_per_casting_kwh_kg':
      return formatKwhPerKg(n);
    case 'blast_time_sec':
      return secondsToHoursMin(n);
    case 'cycle_count':
      return isFinite(n) ? Math.round(n).toLocaleString() : raw;
    case 'avg_shot_refill_time_sec':
      return secondsToHoursMin(n);
    case 'last_refill_epoch_sec':
      return epochToLocalDatetime(n);
    default:
      return raw;
  }
}
