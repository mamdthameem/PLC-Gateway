import type { TrendBucket } from '../types';

const HOUR_MS = 3_600_000;
const DAY_MS  = 86_400_000;

/**
 * Axis label matched to the bucket size.
 *
 * Deliberately short — these are printed at an angle under a dense axis, and a long label forces
 * Recharts to drop ticks, which is how an axis ends up with uneven gaps between the labels that
 * survive.
 *
 * Bucket SELECTION no longer lives here. It moved to the server (`bucket=auto`), which is the only
 * place that knows how much history exists: this module used to answer "no bounds ⇒ month", so
 * every all-time graph asked for months even when the plant had recorded a fortnight, and a
 * 29-day history was drawn as two bars covering 19 days and 6 days.
 */
export function formatBucketLabel(iso: string, bucket: TrendBucket): string {
  const d = new Date(iso);
  switch (bucket) {
    case 'hour':
      return d.toLocaleString(undefined, { day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit' });
    case 'month':
      return d.toLocaleDateString(undefined, { month: 'short', year: 'numeric' });
    default:
      return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
  }
}

/**
 * Full label for a tooltip — names the whole interval the point covers, not just its start.
 *
 * A bar labelled "10 Aug" is a whole day of production; without the interval spelled out
 * somewhere, a reader cannot tell whether the point is an instant or a total.
 */
export function formatBucketFull(iso: string, bucket: TrendBucket): string {
  const d = new Date(iso);
  switch (bucket) {
    case 'hour': {
      const end = new Date(d.getTime() + HOUR_MS);
      const hm  = { hour: '2-digit', minute: '2-digit' } as const;
      return `${d.toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' })}, ` +
             `${d.toLocaleTimeString(undefined, hm)}–${end.toLocaleTimeString(undefined, hm)}`;
    }
    case 'month':
      return d.toLocaleDateString(undefined, { month: 'long', year: 'numeric' });
    default:
      return d.toLocaleDateString(undefined, {
        weekday: 'long', day: 'numeric', month: 'long', year: 'numeric',
      });
  }
}

export { HOUR_MS, DAY_MS };
