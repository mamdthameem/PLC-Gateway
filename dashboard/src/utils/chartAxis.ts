import type { TrendBucket } from '../types';

/**
 * Shared axis construction for every chart on the dashboard.
 *
 * Two rules, applied identically everywhere, are what make the charts read as one system:
 *
 *  1. EQUAL SPACING MEANS EQUAL INTERVAL. A category axis is only honest when every category
 *     covers the same span, so /api/trends now gap-fills its bucket grid (a day the plant did not
 *     run comes back as zeros rather than being omitted). Before that, a series with four missing
 *     Sundays was drawn as evenly-spaced points that were NOT evenly spaced in time.
 *
 *  2. TICKS ARE STRIDED, NOT GUESSED. Recharts' interval="preserveStartEnd" thins labels by
 *     whatever fits, so the gap between printed ticks varies across the axis. Striding by a fixed
 *     number of categories gives ticks at a constant interval, which is what lets a reader measure
 *     distance along the axis.
 *
 * Every axis also carries a title naming its unit or its interval. An axis without one is the
 * single most common reason a chart cannot be read.
 */

/** Axis title for the x-axis of a bucketed trend chart. */
/** Granularity as an adjective, for subtitles and series names ("Daily Production"). */
export const BUCKET_ADJECTIVE: Record<TrendBucket, string> = {
  hour:  'Hourly',
  day:   'Daily',
  month: 'Monthly',
};

/**
 * Recharts' `interval` prop for a category axis: the number of categories to SKIP between printed
 * ticks. Returns a stride that yields at most `target` labels, evenly spaced.
 *
 * `interval={0}` prints EVERY label, and that is the goal wherever the labels physically fit —
 * a chart captioned "one point per day" should name every day, not every third one.
 *
 * The default target of 31 is a full month of daily buckets. Why that many fit: inside the `lg`
 * dialog the plot area is ~1 030 px (1 200 px dialog − padding − the 78 px y-axis − margins).
 * A tilted "10 Aug" at 10 px is ~41 px long, and a label rotated 45° occupies only
 * `length × cos(45°)` ≈ 29 px of horizontal room — so ~35 labels clear each other. 31 keeps a
 * margin and covers the common all-time case exactly.
 *
 * Striding still kicks in beyond that, because it has to: 400 daily buckets or 1 433 cycles cannot
 * each carry a legible label at any font size. Pass a smaller `target` for axes whose labels are
 * NOT rotated — horizontal text needs its full width, so far fewer of them fit.
 */
export function tickInterval(count: number, target = 31): number {
  if (count <= target) return 0;
  return Math.ceil(count / target) - 1;
}

/**
 * Round tick positions across a NUMERIC axis — 100, 200, 300… rather than wherever the data
 * happens to start.
 *
 * Recharts' `interval` prop strides by data INDEX, so on a cycle axis running 1…1463 it printed
 * 1, 106, 211, 316 — every 105th row, which is uniform but unreadable. Nobody looks up "cycle 211".
 * These are positional ticks on a `type="number"` axis, so they land on round cycle numbers and
 * stay round whatever the range turns out to be.
 *
 * The target is deliberately generous (15): it pushes the chosen step down to the next round
 * number, so 1463 cycles get a step of 100 rather than 200.
 */
export function numericTicks(min: number, max: number, target = 15): number[] {
  const span = max - min;
  if (!Number.isFinite(span) || span <= 0) return [min];

  const rawStep = span / target;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const normalised = rawStep / magnitude;
  const niceNormalised =
    normalised <= 1   ? 1 :
    normalised <= 2   ? 2 :
    normalised <= 2.5 ? 2.5 :
    normalised <= 5   ? 5 : 10;

  const step = niceNormalised * magnitude;
  const ticks: number[] = [];
  for (let v = Math.ceil(min / step) * step; v <= max; v += step) {
    ticks.push(Number(v.toPrecision(12)));
  }
  return ticks;
}

/**
 * A rounded y-axis domain starting at zero, plus uniformly spaced ticks to match.
 *
 * Recharts' automatic domain picks the data max and divides it, so the top gridline lands on a
 * number like 4 731.6 and the spacing between gridlines is an arbitrary fraction. Snapping the
 * step to 1 / 2 / 2.5 / 5 × a power of ten gives gridlines a reader can count in their head, and
 * anchoring at zero keeps bar heights proportional to their values.
 */
export function niceScale(maxValue: number, tickCount = 5): { domain: [number, number]; ticks: number[] } {
  if (!Number.isFinite(maxValue) || maxValue <= 0) {
    return { domain: [0, 1], ticks: [0, 0.25, 0.5, 0.75, 1] };
  }

  const rawStep = maxValue / tickCount;
  const magnitude = Math.pow(10, Math.floor(Math.log10(rawStep)));
  const normalised = rawStep / magnitude;
  const niceNormalised =
    normalised <= 1   ? 1 :
    normalised <= 2   ? 2 :
    normalised <= 2.5 ? 2.5 :
    normalised <= 5   ? 5 : 10;

  const step = niceNormalised * magnitude;
  const top  = Math.ceil(maxValue / step) * step;

  const ticks: number[] = [];
  // Half a step of slack absorbs the float error that would otherwise drop the topmost tick.
  for (let v = 0; v <= top + step / 2; v += step) {
    ticks.push(Number(v.toPrecision(12)));
  }

  return { domain: [0, top], ticks };
}

/** niceScale over a series, reading the max off one numeric field. */
export function niceScaleOf<T>(rows: T[], pick: (row: T) => number | null | undefined, tickCount = 5) {
  const max = rows.reduce((m, r) => {
    const v = pick(r);
    return typeof v === 'number' && Number.isFinite(v) && v > m ? v : m;
  }, 0);
  return niceScale(max, tickCount);
}

/**
 * Shared x-axis geometry, so every trend chart tilts and spaces its labels identically.
 *
 * Takes the point count because label density and legibility trade off against each other: a
 * dense axis tilts a degree steeper and drops a point of font size, which is what buys the room
 * to print every bucket rather than every third one. Includes `interval`, so a caller cannot
 * accidentally pair this geometry with a different stride.
 */
export function categoryXAxis(count: number) {
  const dense = count > 20;
  return {
    tick: { fontSize: dense ? 10 : 11 },
    angle: -45 as const,
    textAnchor: 'end' as const,
    height: dense ? 78 : 64,
    tickMargin: 6,
    minTickGap: 0,
    interval: tickInterval(count),
  };
}

/** Shared y-axis geometry. */
export const Y_AXIS = {
  tick: { fontSize: 11 },
  width: 78,
  tickMargin: 4,
};

/** Builds the `label` prop for a y-axis title, rotated into the margin. */
export function yAxisTitle(value: string) {
  return {
    value,
    angle: -90 as const,
    position: 'insideLeft' as const,
    offset: 8,
    style: { fontSize: 12, textAnchor: 'middle' as const },
  };
}

/** Builds the `label` prop for an x-axis title, below the tilted tick labels. */
export function xAxisTitle(value: string) {
  return {
    value,
    position: 'insideBottom' as const,
    offset: -4,
    style: { fontSize: 12, textAnchor: 'middle' as const },
  };
}

/** Chart body height inside an expanded dialog. Tall enough that a dense series stays readable. */
export const CHART_HEIGHT = 420;

/** Margin that leaves room for both axis titles without clipping the tilted x labels. */
export const CHART_MARGIN = { top: 12, right: 24, left: 12, bottom: 44 };
