import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip,
  ReferenceLine, ResponsiveContainer,
} from 'recharts';
import { fetchTrends } from '../services/trendsService';
import { pickBucket, formatBucketLabel } from '../utils/trendBuckets';

interface Props {
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

export default function UtilityGraph({ windowStart, windowEnd }: Props) {
  const [data, setData]       = useState<{ label: string; utilityPct: number }[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const start = windowStart ? new Date(windowStart) : undefined;
    const end   = windowEnd   ? new Date(windowEnd)   : undefined;
    const bucket = pickBucket(start, end);

    // The server does the bucketing and the utility arithmetic — this component only plots.
    fetchTrends(bucket, start, end)
      .then(rows => {
        if (!active) return;
        setData(rows.map(r => ({
          label:      formatBucketLabel(r.day, bucket),
          utilityPct: r.utilityPct,
        })));
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [windowStart, windowEnd]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!data.length) return <Typography color="text.secondary">No utility data recorded yet.</Typography>;

  return (
    <Box sx={{ width: '100%', height: 320 }}>
      <ResponsiveContainer>
        <LineChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 32 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis
            dataKey="label"
            tick={{ fontSize: 10 }}
            angle={-40}
            textAnchor="end"
            interval="preserveStartEnd"
            minTickGap={12}
          />
          <YAxis domain={[0, 100]} unit="%" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(1)} %`, 'Utility']} />
          <ReferenceLine y={80} stroke="#f59e0b" strokeDasharray="4 4" label={{ value: '80%', fontSize: 10 }} />
          <Line type="monotone" dataKey="utilityPct" stroke="#1976d2" dot={false} strokeWidth={2} name="Utility %" />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
