import type { HistoricalPoint } from '../types';

export interface UtilityBucket {
  time: Date;
  label: string;
  utilityPct: number;
}

export const HOUR_MS = 3_600_000;
export const DAY_MS  = 86_400_000;

function isBlastOn(val: string): boolean {
  return val === '1' || val.toLowerCase() === 'true';
}

function isMachineOn(val: string): boolean {
  return val !== '0';
}

function bucketFloor(t: number, bucketMs: number): number {
  const d = new Date(t);
  if (bucketMs >= DAY_MS) {
    return new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  }
  return new Date(d.getFullYear(), d.getMonth(), d.getDate(), d.getHours()).getTime();
}

function contributeInterval(
  startMs: number,
  endMs: number,
  blastOn: boolean,
  machineOn: boolean,
  buckets: Map<number, { blastSec: number; machineSec: number }>,
  bucketMs: number
) {
  if (!machineOn || endMs <= startMs) {
    if (machineOn && endMs > startMs) {
      let t = startMs;
      while (t < endMs) {
        const bTs    = bucketFloor(t, bucketMs);
        const segEnd = Math.min(endMs, bTs + bucketMs);
        const sec    = (segEnd - t) / 1000;
        const b      = buckets.get(bTs) ?? { blastSec: 0, machineSec: 0 };
        b.machineSec += sec;
        buckets.set(bTs, b);
        t = segEnd;
      }
    }
    return;
  }
  let t = startMs;
  while (t < endMs) {
    const bTs    = bucketFloor(t, bucketMs);
    const segEnd = Math.min(endMs, bTs + bucketMs);
    const sec    = (segEnd - t) / 1000;
    const b      = buckets.get(bTs) ?? { blastSec: 0, machineSec: 0 };
    b.machineSec += sec;
    if (blastOn) b.blastSec += sec;
    buckets.set(bTs, b);
    t = segEnd;
  }
}

export function computeUtility(
  blastRecords: HistoricalPoint[],
  machineRecords: HistoricalPoint[],
  periodStart: Date,
  periodEnd: Date,
  bucketMs: number = HOUR_MS
): UtilityBucket[] {
  type Ev = { timeMs: number; type: 'blast' | 'machine'; value: string };

  const events: Ev[] = [
    ...blastRecords.map(r  => ({ timeMs: new Date(r.timestamp).getTime(), type: 'blast'   as const, value: r.value })),
    ...machineRecords.map(r => ({ timeMs: new Date(r.timestamp).getTime(), type: 'machine' as const, value: r.value })),
  ].sort((a, b) => a.timeMs - b.timeMs);

  let machineOn = false;
  let blastOn   = false;
  const buckets = new Map<number, { blastSec: number; machineSec: number }>();
  const startMs = periodStart.getTime();
  const endMs   = periodEnd.getTime();

  for (const ev of events) {
    if (ev.timeMs >= startMs) break;
    if (ev.type === 'blast')   blastOn   = isBlastOn(ev.value);
    else                       machineOn = isMachineOn(ev.value);
  }

  let prevMs = startMs;
  for (const ev of events) {
    if (ev.timeMs < startMs) continue;
    const evMs = Math.min(ev.timeMs, endMs);
    contributeInterval(prevMs, evMs, blastOn, machineOn, buckets, bucketMs);
    if (ev.timeMs > endMs) break;
    if (ev.type === 'blast')   blastOn   = isBlastOn(ev.value);
    else                       machineOn = isMachineOn(ev.value);
    prevMs = evMs;
  }
  contributeInterval(prevMs, endMs, blastOn, machineOn, buckets, bucketMs);

  const isDaily = bucketMs >= DAY_MS;
  return Array.from(buckets.entries())
    .sort(([a], [b]) => a - b)
    .map(([bTs, { blastSec, machineSec }]) => ({
      time:       new Date(bTs),
      label:      new Date(bTs).toLocaleDateString(undefined, isDaily
                    ? { month: 'short', day: 'numeric' }
                    : { hour: '2-digit', minute: '2-digit' }),
      utilityPct: machineSec > 0 ? Math.min(100, (blastSec / machineSec) * 100) : 0,
    }));
}

/** @deprecated Use computeUtility */
export function computeHourlyUtility(
  blastRecords: HistoricalPoint[],
  machineRecords: HistoricalPoint[],
  periodStart: Date,
  periodEnd: Date
): UtilityBucket[] {
  return computeUtility(blastRecords, machineRecords, periodStart, periodEnd, HOUR_MS);
}
