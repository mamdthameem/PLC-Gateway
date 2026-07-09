import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
} from 'recharts';
import { fetchHistorical } from '../services/historicalService';

interface Props {
  windowStart: string;
  windowEnd: string;
}

export default function ProductionGraph({ windowStart, windowEnd }: Props) {
  const [data, setData]     = useState<{ label: string; kg: number }[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    fetchHistorical('Tonnage', new Date(windowStart), new Date(windowEnd))
      .then(points => {
        if (!active) return;
        const chartData = points
          .map(p => {
            const v = parseFloat(p.value);
            return isFinite(v) ? {
              label: new Date(p.timestamp).toLocaleString(undefined, {
                month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit',
              }),
              kg: v,
            } : null;
          })
          .filter((x): x is { label: string; kg: number } => x !== null);
        setData(chartData);
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
  if (!data.length) return <Typography color="text.secondary">No production data for this period.</Typography>;

  return (
    <Box sx={{ width: '100%', height: 320 }}>
      <ResponsiveContainer>
        <LineChart data={data} margin={{ top: 8, right: 16, left: 0, bottom: 24 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="label" tick={{ fontSize: 10 }} angle={-45} textAnchor="end" interval="preserveStartEnd" />
          <YAxis unit=" kg" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toLocaleString()} kg`, 'Tonnage']} />
          <Line type="monotone" dataKey="kg" stroke="#2e7d32" dot={false} strokeWidth={2} name="Tonnage (kg)" />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
