import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip,
  ReferenceLine, ResponsiveContainer,
} from 'recharts';
import { fetchHistorical } from '../services/historicalService';
import { computeUtility, DAY_MS, HOUR_MS } from '../utils/utilityCompute';

interface Props {
  windowStart: string;
  windowEnd: string;
}

export default function UtilityGraph({ windowStart, windowEnd }: Props) {
  const [data, setData]     = useState<{ label: string; utilityPct: number }[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const start    = new Date(windowStart);
    const end      = new Date(windowEnd);
    const windowMs = end.getTime() - start.getTime();
    const bucketMs = windowMs > DAY_MS ? DAY_MS : HOUR_MS;
    const lookback = new Date(start.getTime() - bucketMs);

    Promise.all([
      fetchHistorical('Blast ON/OFF',   lookback, end),
      fetchHistorical('Machine status', lookback, end),
    ])
      .then(([blastRec, machineRec]) => {
        if (!active) return;
        const buckets = computeUtility(blastRec, machineRec, start, end, bucketMs);
        setData(buckets.map(b => ({ label: b.label, utilityPct: parseFloat(b.utilityPct.toFixed(1)) })));
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
  if (!data.length) return <Typography color="text.secondary">No utility data for this period.</Typography>;

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
            label={{ value: 'Date', position: 'insideBottom', offset: -20, fontSize: 11 }}
          />
          <YAxis domain={[0, 100]} unit="%" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(1)} %`, 'Utility']} />
          <ReferenceLine y={80} stroke="#f59e0b" strokeDasharray="4 4" label={{ value: '80%', fontSize: 10 }} />
          <Line type="monotone" dataKey="utilityPct" stroke="#1976d2" dot={{ r: 3 }} strokeWidth={2} name="Utility %" />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
