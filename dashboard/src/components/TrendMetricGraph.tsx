import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip } from 'recharts';
import TrendChartFrame from './TrendChartFrame';
import { useTrendSeries, type TrendPoint } from '../utils/useTrendSeries';
import {
  categoryXAxis, Y_AXIS, CHART_MARGIN, niceScaleOf,
  xAxisTitle, yAxisTitle,
} from '../utils/chartAxis';

export type TrendMetric = 'energy' | 'blastTime' | 'cycleCount';

interface Props {
  metric: TrendMetric;
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

interface MetricSpec {
  pick:   (p: TrendPoint) => number;
  axis:   string;
  /** Series name, used by the tooltip only — a single-series chart carries no legend. */
  series: string;
  color:  string;
  format: (v: number) => string;
  empty:  string;
}

/**
 * Every per-bucket quantity that comes out of the daily rollup, drawn the same way.
 *
 * One component rather than three because the complaint that started this was a lack of
 * uniformity: separate implementations drifted into different tick densities, different label
 * angles and different tooltip wording for charts that a reader compares side by side.
 *
 * Blast time and blast-cycle counts are plotted here for the first time. plc_daily_trends has
 * carried blast_on_sec and cycle_count all along — the tiles were scalar-only because no chart had
 * been built, not because the data was missing.
 */
const SPECS: Record<TrendMetric, MetricSpec> = {
  energy: {
    pick:    p => p.energyKwh,
    axis:    'Energy (kWh)',
    series:  'Energy',
    color:   '#1565c0',
    format:  v => `${v.toLocaleString(undefined, { maximumFractionDigits: 2 })} kWh`,
    empty:   'No energy data recorded yet.',
  },
  blastTime: {
    // Seconds are what the rollup stores, but nobody reads a bar 28 800 units tall. Hours are the
    // same unit the Blast Time tile shows, so the chart and the tile agree at a glance.
    pick:    p => p.blastOnSec / 3600,
    axis:    'Blast Time (hours)',
    series:  'Blast Time',
    color:   '#6a1b9a',
    format:  v => `${v.toFixed(2)} h`,
    empty:   'No blast time recorded yet.',
  },
  cycleCount: {
    pick:    p => p.cycleCount,
    axis:    'Blast Cycles',
    series:  'Blast Cycles',
    color:   '#00838f',
    format:  v => `${Math.round(v).toLocaleString()} cycles`,
    empty:   'No blast cycles recorded yet.',
  },
};

export default function TrendMetricGraph({ metric, windowStart, windowEnd }: Props) {
  const state = useTrendSeries(windowStart, windowEnd);
  const { points } = state;
  const spec = SPECS[metric];

  const data = points.map(p => ({ ...p, value: spec.pick(p) }));
  const y = niceScaleOf(data, d => d.value);

  return (
    <TrendChartFrame state={state} emptyMessage={spec.empty}>
      <BarChart data={data} margin={CHART_MARGIN}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis
          {...categoryXAxis(points.length)}
          dataKey="label"
          label={xAxisTitle('Date')}
        />
        <YAxis
          {...Y_AXIS}
          domain={y.domain}
          ticks={y.ticks}
          tickFormatter={(v: number) => v.toLocaleString()}
          label={yAxisTitle(spec.axis)}
        />
        <Tooltip
          formatter={(v: number | undefined) => [spec.format(v ?? 0), spec.series]}
          labelFormatter={(_l, payload) => payload?.[0]?.payload?.full ?? ''}
        />
        <Bar dataKey="value" name={spec.series} fill={spec.color} radius={[2, 2, 0, 0]} />
      </BarChart>
    </TrendChartFrame>
  );
}
