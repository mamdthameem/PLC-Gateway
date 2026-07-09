import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  LineChart, Line, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer,
} from 'recharts';
import { fetchHistorical } from '../services/historicalService';
import { fetchLatestCycle } from '../services/cyclesService';

interface Props {
  impellerNumber: number;   // 1–10
  windowStart?: string;
  windowEnd?: string;
}

const COLORS = [
  '#1976d2','#d32f2f','#388e3c','#f57c00','#7b1fa2',
  '#0097a7','#c2185b','#5d4037','#455a64','#fbc02d',
];

interface Row { label: string; amps?: number }

export default function AmpsGraph({ impellerNumber, windowStart, windowEnd }: Props) {
  const [rows, setRows]       = useState<Row[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  const paramName = `Current_imp_${impellerNumber}`;
  const color     = COLORS[(impellerNumber - 1) % COLORS.length];

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    async function load() {
      try {
        let start: Date;
        let end: Date;

        if (windowStart && windowEnd) {
          start = new Date(windowStart);
          end   = new Date(windowEnd);
        } else {
          const cycle = await fetchLatestCycle();
          if (!cycle) {
            if (active) { setError('No completed blast cycle found.'); setLoading(false); }
            return;
          }
          start = new Date(cycle.blastStart);
          end   = new Date(cycle.blastEnd);
        }

        const records = await fetchHistorical(paramName, start, end);
        if (!active) return;

        // Forward-fill: carry last known value across all timestamps
        let lastVal: number | undefined;
        const chartRows: Row[] = records.map(r => {
          const v = parseFloat(r.value);
          if (isFinite(v)) lastVal = v;
          return {
            label: new Date(r.timestamp).toLocaleTimeString(undefined, {
              hour: '2-digit', minute: '2-digit', second: '2-digit',
            }),
            amps: lastVal,
          };
        });

        setRows(chartRows);
        setLoading(false);
      } catch (e) {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      }
    }

    load();
    return () => { active = false; };
  }, [impellerNumber, windowStart, windowEnd, paramName]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!rows.length) return <Typography color="text.secondary">No data for this impeller in the last cycle.</Typography>;

  return (
    <Box sx={{ width: '100%', height: 300 }}>
      <ResponsiveContainer>
        <LineChart data={rows} margin={{ top: 8, right: 16, left: 0, bottom: 8 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="label" tick={{ fontSize: 9 }} interval="preserveStartEnd" />
          <YAxis unit=" A" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(2)} A`, `Impeller ${impellerNumber}`]} />
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
