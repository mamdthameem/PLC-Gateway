import { Box, Typography } from '@mui/material';
import { BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer } from 'recharts';
import type { ShotsBreakdownEntry } from '../types';

interface Props {
  data: ShotsBreakdownEntry[];
}

export default function ShotsBreakdownChart({ data }: Props) {
  if (data.length === 0) {
    return (
      <Box sx={{ py: 2, textAlign: 'center' }}>
        <Typography variant="body2" color="text.secondary">No shots breakdown data available.</Typography>
      </Box>
    );
  }

  // Refills can land seconds apart (rapid refill activity produces a burst of events on one day).
  // A date-only label collapses every bar in that burst to the same text — e.g. 298 bars all
  // reading "21 Jan" — so the time of day has to be part of the label whenever the series does
  // not span multiple days.
  const first = new Date(data[0].refillTimestamp);
  const last  = new Date(data[data.length - 1].refillTimestamp);
  const sameDay = first.toDateString() === last.toDateString();

  const chartData = data.map(d => {
    const t = new Date(d.refillTimestamp);
    return {
      label: sameDay
        ? t.toLocaleTimeString(undefined, { hour: '2-digit', minute: '2-digit', second: '2-digit' })
        : t.toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }),
      // Full timestamp for the tooltip, so a thinned axis never hides which refill a bar is.
      full: t.toLocaleString(),
      blastCount: d.blastCount,
    };
  });

  return (
    <Box sx={{ width: '100%', height: 260 }}>
      <ResponsiveContainer width="100%" height="100%">
        <BarChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 28 }}>
          <CartesianGrid strokeDasharray="3 3" />
          {/* minTickGap thins the labels instead of printing all of them on top of each other. */}
          <XAxis
            dataKey="label"
            tick={{ fontSize: 10 }}
            angle={-40}
            textAnchor="end"
            interval="preserveStartEnd"
            minTickGap={24}
          />
          <YAxis tick={{ fontSize: 11 }} allowDecimals={false} />
          <Tooltip
            formatter={(value: number | undefined) => [value != null ? value.toLocaleString() : '—', 'Blasts']}
            labelFormatter={(_label, payload) => payload?.[0]?.payload?.full ?? ''}
          />
          <Bar dataKey="blastCount" name="Blast Count" fill="#1976d2" radius={[3, 3, 0, 0]} />
        </BarChart>
      </ResponsiveContainer>
    </Box>
  );
}
