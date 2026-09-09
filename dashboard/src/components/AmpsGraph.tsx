import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer,
} from 'recharts';
import { fetchPerCycleAmps } from '../services/cyclesService';
import {
  Y_AXIS, CHART_MARGIN, CHART_HEIGHT, niceScale, numericTicks, xAxisTitle, yAxisTitle,
} from '../utils/chartAxis';

interface Props {
  impellerNumber: number;   // 1–10
}

// Exported so FilteredAmpsGraph (Section 2) uses the same per-impeller color as Section 1.
export const IMPELLER_COLORS = [
  '#1976d2','#d32f2f','#388e3c','#f57c00','#7b1fa2',
  '#0097a7','#c2185b','#5d4037','#455a64','#fbc02d',
];

interface Row { cycle: number; amps: number | null; when: string }

/**
 * Average current per completed blast cycle for one impeller, across the whole recorded history.
 *
 * ONE POINT PER CYCLE, on a cycle-number axis — so there are no zeros between points, and there
 * should not be. The zeros belong to a TIME axis, where the gap between one blast ending and the
 * next starting is real elapsed time with the impellers stopped. Here the x-axis is a sequence of
 * cycles: nothing exists "between" cycle 960 and cycle 961 to plot, and drawing a dip to zero
 * would invent a data point that does not exist.
 *
 * A per-sample view (raw current, showing the ramp from 0 and the fall back to it) was offered
 * alongside this on 5 / 25 / 100-cycle ranges and removed as redundant. If the spin-up shape is
 * ever wanted again, it needs a time axis over a bounded window — it cannot be layered onto this
 * chart, because per-second detail across 1 463 cycles is ~178 000 samples for a single impeller.
 */
export default function AmpsGraph({ impellerNumber }: Props) {
  const [rows, setRows]       = useState<Row[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  const color = IMPELLER_COLORS[(impellerNumber - 1) % IMPELLER_COLORS.length];

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    fetchPerCycleAmps(impellerNumber)
      .then(data => {
        if (!active) return;
        setRows(data.map(c => ({
          cycle: c.cycleNumber,
          amps:  c.avgAmps,
          when:  new Date(c.blastEnd).toLocaleString(),
        })));
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [impellerNumber]);

  if (loading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: CHART_HEIGHT }}>
        <CircularProgress />
      </Box>
    );
  }
  if (error) return <Alert severity="error">{error}</Alert>;
  if (!rows.length) return <Typography color="text.secondary">No completed cycles recorded.</Typography>;

  const y = niceScale(Math.max(...rows.map(r => r.amps ?? 0)), 5);
  const first = rows[0].cycle;
  const last  = rows[rows.length - 1].cycle;

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
        Average current per cycle, impeller {impellerNumber}. All {rows.length.toLocaleString()} recorded
        cycles are plotted. Current climbs as the blades wear and steps back down when they are replaced.
      </Typography>

      <Box sx={{ width: '100%', height: CHART_HEIGHT }}>
        <ResponsiveContainer>
          <LineChart data={rows} margin={CHART_MARGIN}>
            <CartesianGrid strokeDasharray="3 3" />
            {/* type="number" so ticks are POSITIONAL and land on round cycle numbers. As a category
                axis Recharts strides by row index instead, which printed 1, 106, 211, 316… */}
            <XAxis
              dataKey="cycle"
              type="number"
              domain={[first, last]}
              ticks={numericTicks(first, last)}
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
              label={yAxisTitle('Average current (A)')}
            />
            <Tooltip
              formatter={(v: number | undefined) => [v != null ? `${v.toFixed(2)} A` : '—', `Impeller ${impellerNumber}`]}
              labelFormatter={(label, payload) => `Cycle ${label}, ended ${payload?.[0]?.payload?.when ?? ''}`}
            />
            <Legend wrapperStyle={{ fontSize: 12 }} verticalAlign="top" height={28} />
            <Line
              type="monotone"
              dataKey="amps"
              stroke={color}
              dot={false}
              strokeWidth={1.5}
              name={`Impeller ${impellerNumber} average current (A)`}
              connectNulls
            />
          </LineChart>
        </ResponsiveContainer>
      </Box>
    </Box>
  );
}
