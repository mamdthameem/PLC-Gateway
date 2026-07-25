import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  BarChart, Bar, LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
} from 'recharts';
import { fetchTrends } from '../services/trendsService';
import { pickBucket, formatBucketLabel } from '../utils/trendBuckets';

interface Props {
  mode: 'energy' | 'efficiency';
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

/**
 * All-time energy / efficiency trend for Section 1, read from the daily rollup.
 *
 * Section 2 keeps its own per-cycle chart (CycleDataGraph) because a filtered window is bounded
 * and per-cycle detail is meaningful there. Over all time it would not be: thousands of cycles
 * cannot be read as bars, and the old Section 1 chart silently truncated to the last 100.
 */
export default function EnergyTrendGraph({ mode, windowStart, windowEnd }: Props) {
  const [data, setData]       = useState<{ label: string; value: number }[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const start = windowStart ? new Date(windowStart) : undefined;
    const end   = windowEnd   ? new Date(windowEnd)   : undefined;
    const bucket = pickBucket(start, end);

    fetchTrends(bucket, start, end)
      .then(rows => {
        if (!active) return;
        setData(rows.map(r => ({
          label: formatBucketLabel(r.day, bucket),
          value: mode === 'energy' ? r.energyKwh : r.efficiencyKwhPerKg,
        })));
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [mode, windowStart, windowEnd]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!data.length) return <Typography color="text.secondary">No energy data recorded yet.</Typography>;

  const axis = (
    <>
      <CartesianGrid strokeDasharray="3 3" />
      <XAxis
        dataKey="label"
        tick={{ fontSize: 10 }}
        angle={-40}
        textAnchor="end"
        interval="preserveStartEnd"
        minTickGap={12}
      />
    </>
  );

  if (mode === 'energy') {
    return (
      <Box sx={{ width: '100%', height: 320 }}>
        <ResponsiveContainer>
          <BarChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 32 }}>
            {axis}
            <YAxis unit=" kWh" tick={{ fontSize: 11 }} />
            <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(3)} kWh`, 'Energy']} />
            <Bar dataKey="value" fill="#1565c0" radius={[2, 2, 0, 0]} name="Energy (kWh)" />
          </BarChart>
        </ResponsiveContainer>
      </Box>
    );
  }

  return (
    <Box sx={{ width: '100%', height: 320 }}>
      <ResponsiveContainer>
        <LineChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 32 }}>
          {axis}
          <YAxis unit=" kWh/kg" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(4)} kWh/kg`, 'Efficiency']} />
          <Line type="monotone" dataKey="value" stroke="#e65100" dot={false} strokeWidth={2} name="kWh/kg" />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
