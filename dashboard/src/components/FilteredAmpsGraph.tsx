import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
} from 'recharts';
import { fetchFilterAmps } from '../services/filterService';
import { IMPELLER_COLORS } from './AmpsGraph';

interface Props {
  requestId: number;
  impellerNumber: number; // 1–10
}

interface Row { label: string; amps: number | null }

/**
 * Section 2 counterpart to AmpsGraph — same single-line, single-color chart, but one point per
 * cycle in this filter (blast_end as the x-axis) instead of raw per-second samples across "Last
 * Blast Cycle". Raw current samples are far denser than per-cycle data, so a wide time filter
 * plotted verbatim would be unreadable and slow; per-cycle averages are the same grain every
 * other Section 2 graph (energy, efficiency, blast time) already uses.
 */
export default function FilteredAmpsGraph({ requestId, impellerNumber }: Props) {
  const [rows, setRows]       = useState<Row[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  const color = IMPELLER_COLORS[(impellerNumber - 1) % IMPELLER_COLORS.length];

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    fetchFilterAmps(requestId)
      .then(data => {
        if (!active) return;
        const forImpeller = data.find(d => d.impellerNumber === impellerNumber);
        const cycles = forImpeller?.cycles ?? [];

        // Keep the chart readable when a filter spans a very large number of cycles.
        const visible = cycles.slice(-200);

        setRows(visible.map(c => ({
          label: new Date(c.blastEnd).toLocaleString(undefined, {
            month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
          }),
          amps: c.avgAmps,
        })));
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [requestId, impellerNumber]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!rows.length) return <Typography color="text.secondary">No cycle data for this impeller in the filter.</Typography>;

  return (
    <Box sx={{ width: '100%', height: 300 }}>
      <ResponsiveContainer>
        <LineChart data={rows} margin={{ top: 8, right: 16, left: 0, bottom: 8 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="label" tick={{ fontSize: 9 }} interval="preserveStartEnd" />
          <YAxis unit=" A" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [v != null ? `${v.toFixed(2)} A` : '—', `Impeller ${impellerNumber}`]} />
          <Line
            type="monotone"
            dataKey="amps"
            stroke={color}
            dot={false}
            strokeWidth={2}
            name={`Impeller ${impellerNumber}`}
            connectNulls
          />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
