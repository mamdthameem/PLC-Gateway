import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer,
} from 'recharts';
import { fetchFilterCycles } from '../services/filterService';
import {
  Y_AXIS, CHART_MARGIN, CHART_HEIGHT, numericTicks, niceScaleOf, xAxisTitle, yAxisTitle,
} from '../utils/chartAxis';
import type { FilteredCycle } from '../types';

interface Props {
  /**
   * Section 2 only — the completed filter request whose cycles are charted. Section 1 uses
   * TrendMetricGraph instead: it reads the daily rollup rather than submitting a filter request,
   * so opening a Section 1 graph never writes a row to calculation_requests.
   */
  requestId: number;
}

type State = 'loading' | 'done' | 'error';

/** Above this many cycles, bars are narrower than a pixel — a line reads the shape better. */
const LINE_THRESHOLD = 120;

/**
 * Energy consumed per blast cycle within one filtered window.
 *
 * EVERY cycle in the filter is plotted. This used to slice to the most recent 200, which on a
 * month filter silently discarded ~1 250 of 1 448 cycles while the tile above it totalled all of
 * them — the chart and the scalar were describing different sets of cycles, with only a caption to
 * say so.
 *
 * The x-axis is the cycle number, which is a uniform interval by construction: one unit is one
 * cycle, so no cycle is spaced differently from any other. Ticks are strided so the printed
 * numbers sit at a constant distance.
 *
 * Efficiency (kWh/kg) is no longer charted here. Per-cycle efficiency varies by a few thousandths
 * across a filter, so the line was noise magnified by an auto-fitted axis; the tile carries the
 * figure and the Cycle Breakdown table carries the per-cycle detail.
 */
export default function CycleDataGraph({ requestId }: Props) {
  const [cycles, setCycles] = useState<FilteredCycle[]>([]);
  const [state, setState]   = useState<State>('loading');
  const [error, setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setState('loading');
    setError(null);

    fetchFilterCycles(requestId)
      .then(data => {
        if (!active) return;
        setCycles(data);
        setState('done');
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setState('error');
      });

    return () => { active = false; };
  }, [requestId]);

  if (state === 'loading') {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: CHART_HEIGHT }}>
        <CircularProgress />
      </Box>
    );
  }
  if (state === 'error') return <Alert severity="error">{error}</Alert>;
  if (!cycles.length)    return <Typography color="text.secondary">No cycle data in this filter.</Typography>;

  const data = cycles.map(c => ({
    cycle: c.cycleNumber,
    kWh:   Number(c.energyKwh.toFixed(3)),
    start: new Date(c.blastStart).toLocaleString(),
  }));

  const y = niceScaleOf(data, d => d.kWh);
  const asLine = data.length > LINE_THRESHOLD;

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
        One point per blast cycle. All {data.length.toLocaleString()} cycles in this filter are
        plotted, cycles {data[0].cycle.toLocaleString()} to {data[data.length - 1].cycle.toLocaleString()}.
      </Typography>

      <Box sx={{ width: '100%', height: CHART_HEIGHT }}>
        <ResponsiveContainer>
          <ComposedChart data={data} margin={CHART_MARGIN}>
            <CartesianGrid strokeDasharray="3 3" />
            {/* type="number" keeps ticks POSITIONAL, so they land on round cycle numbers.
                A category axis strides by row index and prints whatever cycle sits there. */}
            <XAxis
              dataKey="cycle"
              type="number"
              domain={[data[0].cycle, data[data.length - 1].cycle]}
              ticks={numericTicks(data[0].cycle, data[data.length - 1].cycle)}
              tick={{ fontSize: 11 }}
              height={52}
              tickMargin={6}
              tickFormatter={(v: number) => v.toLocaleString()}
              label={xAxisTitle('Cycle number')}
            />
            <YAxis
              {...Y_AXIS}
              domain={y.domain}
              ticks={y.ticks}
              tickFormatter={(v: number) => v.toLocaleString()}
              label={yAxisTitle('Energy (kWh)')}
            />
            <Tooltip
              formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(3)} kWh`, 'Energy']}
              labelFormatter={(label, payload) =>
                `Cycle ${label}, ${payload?.[0]?.payload?.start ?? ''}`}
            />
            <Legend wrapperStyle={{ fontSize: 12 }} verticalAlign="top" height={28} />
            {asLine ? (
              <Line
                type="monotone"
                dataKey="kWh"
                name="Energy per cycle (kWh)"
                stroke="#1565c0"
                dot={false}
                strokeWidth={1.5}
              />
            ) : (
              <Bar
                dataKey="kWh"
                name="Energy per cycle (kWh)"
                fill="#1565c0"
                radius={[2, 2, 0, 0]}
              />
            )}
          </ComposedChart>
        </ResponsiveContainer>
      </Box>
    </Box>
  );
}
