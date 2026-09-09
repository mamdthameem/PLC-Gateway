import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip,
} from 'recharts';
import TrendChartFrame from './TrendChartFrame';
import { useTrendSeries } from '../utils/useTrendSeries';
import {
  categoryXAxis, Y_AXIS, CHART_MARGIN, niceScale,
  xAxisTitle, yAxisTitle,
} from '../utils/chartAxis';

interface Props {
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

/**
 * Machine utility — blast time as a share of machine on-time, per bucket.
 *
 * There is no longer a dashed 80 % reference line. It was hard-coded, carried no legend entry and
 * sat above a ~74 % series, so it read as a second, unexplained data line. No target utility was
 * ever agreed with the client; inventing one on the chart is worse than showing none.
 *
 * The y-axis is pinned to 0–100 rather than fitted to the data: utility is a percentage of a fixed
 * whole, and auto-fitting it to a 73–76 % range magnifies ordinary variation into what looks like
 * a collapse.
 */
export default function UtilityGraph({ windowStart, windowEnd }: Props) {
  const state = useTrendSeries(windowStart, windowEnd);
  const { points } = state;

  const y = niceScale(100, 5);

  return (
    <TrendChartFrame state={state} emptyMessage="No utility data recorded yet.">
      <LineChart data={points} margin={CHART_MARGIN}>
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
          tickFormatter={(v: number) => `${v}`}
          label={yAxisTitle('Machine Utility (%)')}
        />
        <Tooltip
          formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(1)} %`, 'Machine Utility']}
          labelFormatter={(_l, payload) => payload?.[0]?.payload?.full ?? ''}
        />
        <Line
          type="monotone"
          dataKey="utilityPct"
          stroke="#1976d2"
          dot={points.length <= 45 ? { r: 2 } : false}
          strokeWidth={2}
          name="Machine Utility"
        />
      </LineChart>
    </TrendChartFrame>
  );
}
