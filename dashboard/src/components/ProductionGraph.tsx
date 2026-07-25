import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  ComposedChart, Bar, Line, XAxis, YAxis, CartesianGrid, Tooltip, Legend, ResponsiveContainer,
} from 'recharts';
import { fetchTrends } from '../services/trendsService';
import { pickBucket, formatBucketLabel } from '../utils/trendBuckets';

interface Props {
  /** Omit both bounds for the all-time series (Section 1). */
  windowStart?: string;
  windowEnd?: string;
}

interface Row {
  label: string;
  producedKg: number;
  tonnageEnd: number | null;
}

export default function ProductionGraph({ windowStart, windowEnd }: Props) {
  const [data, setData]       = useState<Row[]>([]);
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
          label:      formatBucketLabel(r.day, bucket),
          producedKg: r.productionKg,
          tonnageEnd: r.tonnageEnd,
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
  if (!data.length) return <Typography color="text.secondary">No production data recorded yet.</Typography>;

  return (
    <Box sx={{ width: '100%', height: 320 }}>
      <ResponsiveContainer>
        {/* Bars = produced in each bucket; line = the PLC's running Tonnage accumulator, which is
            what the Production tile itself shows. */}
        <ComposedChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 32 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis
            dataKey="label"
            tick={{ fontSize: 10 }}
            angle={-40}
            textAnchor="end"
            interval="preserveStartEnd"
            minTickGap={12}
          />
          <YAxis yAxisId="left" unit=" kg" tick={{ fontSize: 11 }} />
          <YAxis yAxisId="right" orientation="right" unit=" kg" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => `${(v ?? 0).toLocaleString()} kg`} />
          <Legend wrapperStyle={{ fontSize: 11 }} />
          <Bar
            yAxisId="left"
            dataKey="producedKg"
            name="Produced in period"
            fill="#2e7d32"
            radius={[2, 2, 0, 0]}
          />
          <Line
            yAxisId="right"
            type="monotone"
            dataKey="tonnageEnd"
            name="Tonnage (cumulative)"
            stroke="#1565c0"
            dot={false}
            strokeWidth={2}
            connectNulls
          />
        </ComposedChart>
      </ResponsiveContainer>
    </Box>
  );
}
