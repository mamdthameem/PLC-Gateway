import type { TrendBucket } from '../types';

const HOUR_MS = 3_600_000;
const DAY_MS  = 86_400_000;

/**
 * Chooses the bucket granularity for a graph window.
 *
 * No bounds means the all-time Section 1 series: months, so a decade of recording is ~120 points
 * instead of ~3,650. Short windows get hourly detail (a one-hour filter would otherwise collapse
 * to a single daily point). Everything in between is daily.
 */
export function pickBucket(start?: Date, end?: Date): TrendBucket {
  if (!start || !end) return 'month';

  const span = end.getTime() - start.getTime();
  if (span <= 2 * DAY_MS)   return 'hour';
  if (span <= 180 * DAY_MS) return 'day';
  return 'month';
}

/** Axis label matched to the bucket size. */
export function formatBucketLabel(iso: string, bucket: TrendBucket): string {
  const d = new Date(iso);
  switch (bucket) {
    case 'hour':
      return d.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit' });
    case 'month':
      return d.toLocaleDateString(undefined, { month: 'short', year: 'numeric' });
    default:
      return d.toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
  }
}

export { HOUR_MS, DAY_MS };
