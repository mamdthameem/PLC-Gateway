import { Box, Typography } from '@mui/material';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import type { ShotsBreakdownEntry } from '../types';

interface Props {
  data: ShotsBreakdownEntry[];
}

function formatTimestamp(ts: string): string {
  return new Date(ts).toLocaleDateString(undefined, { month: 'short', day: 'numeric' });
}

export default function ShotsBreakdownChart({ data }: Props) {
  if (data.length === 0) {
    return (
      <Box sx={{ py: 2, textAlign: 'center' }}>
        <Typography variant="body2" color="text.secondary">No shots breakdown data available.</Typography>
      </Box>
    );
  }

  const chartData = data.map(d => ({
    label: formatTimestamp(d.refillTimestamp),
    blastCount: d.blastCount,
  }));

  return (
    <Box sx={{ width: '100%', height: 260 }}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 8 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="label" tick={{ fontSize: 11 }} />
          <YAxis tick={{ fontSize: 11 }} />
          <Tooltip
            formatter={(value: number | undefined) => [value != null ? value.toLocaleString() : '—', 'Blasts']}
          />
          <Bar dataKey="blastCount" name="Blast Count" fill="#1976d2" radius={[3, 3, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </Box>
  );
}
