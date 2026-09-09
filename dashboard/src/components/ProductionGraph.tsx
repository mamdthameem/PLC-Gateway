import {
  ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend,
} from 'recharts';
import TrendChartFrame from './TrendChartFrame';
import { useTrendSeries } from '../utils/useTrendSeries';
import {
  categoryXAxis, Y_AXIS, CHART_MARGIN, niceScaleOf,
  xAxisTitle, yAxisTitle, BUCKET_ADJECTIVE,
} from '../utils/chartAxis';

interface Props {
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

const BAR_COLOR  = '#2e7d32';
const LINE_COLOR = '#1565c0';

/**
 * All-time production: bars are what was produced IN each bucket, the line is the PLC's running
 * Tonnage accumulator AT the end of each bucket.
 *
 * Two series on two scales is the honest way to show this — a per-bucket quantity and a
 * lifetime-to-date total cannot share an axis without one of them being unreadable. What was
 * missing was any way to tell which axis belonged to which series, so each axis is now titled and
 * drawn in its series' own colour, and the legend spells out both.
 *
 * (The bucket size itself is chosen server-side and every bucket in the range is present, so bars
 * are directly comparable to each other. They previously were not: an all-time request always
 * asked for months, so a part-month of recording became a bar covering 6 days sitting next to one
 * covering 19.)
 */
export default function ProductionGraph({ windowStart, windowEnd }: Props) {
  const state = useTrendSeries(windowStart, windowEnd);
  const { points, bucket } = state;

  const left  = niceScaleOf(points, p => p.productionKg);
  const right = niceScaleOf(points, p => p.tonnageEnd ?? 0);

  const kg = (v: number | undefined) =>
    `${(v ?? 0).toLocaleString(undefined, { maximumFractionDigits: 0 })} kg`;

  return (
    <TrendChartFrame state={state} emptyMessage="No production data recorded yet.">
      <ComposedChart data={points} margin={{ ...CHART_MARGIN, right: 78 }}>
        <CartesianGrid strokeDasharray="3 3" />
        <XAxis
          {...categoryXAxis(points.length)}
          dataKey="label"
          label={xAxisTitle('Date')}
        />
        <YAxis
          {...Y_AXIS}
          yAxisId="left"
          domain={left.domain}
          ticks={left.ticks}
          stroke={BAR_COLOR}
          tickFormatter={(v: number) => v.toLocaleString()}
          label={yAxisTitle(`${BUCKET_ADJECTIVE[bucket]} Production (kg)`)}
        />
        <YAxis
          {...Y_AXIS}
          yAxisId="right"
          orientation="right"
          domain={right.domain}
          ticks={right.ticks}
          stroke={LINE_COLOR}
          tickFormatter={(v: number) => v.toLocaleString()}
          label={{
            value: 'Cumulative Production (kg)',
            angle: -90,
            position: 'insideRight',
            offset: 8,
            style: { fontSize: 12, textAnchor: 'middle' as const },
          }}
        />
        <Tooltip
          formatter={(v: number | undefined, name) => [kg(v), name]}
          labelFormatter={(_l, payload) => payload?.[0]?.payload?.full ?? ''}
        />
        <Legend wrapperStyle={{ fontSize: 12 }} verticalAlign="top" height={28} />
        <Bar
          yAxisId="left"
          dataKey="productionKg"
          name={`${BUCKET_ADJECTIVE[bucket]} Production`}
          fill={BAR_COLOR}
          radius={[2, 2, 0, 0]}
        />
        <Line
          yAxisId="right"
          type="monotone"
          dataKey="tonnageEnd"
          name="Cumulative Production"
          stroke={LINE_COLOR}
          dot={false}
          strokeWidth={2}
          connectNulls
        />
      </ComposedChart>
    </TrendChartFrame>
  );
}
