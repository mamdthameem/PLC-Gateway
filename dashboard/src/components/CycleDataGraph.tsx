import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  BarChart, Bar, LineChart, Line, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer,
} from 'recharts';
import { submitFilterRequest, pollFilterStatus, fetchFilterCycles } from '../services/filterService';
import type { FilteredCycle } from '../types';

interface Props {
  mode: 'energy' | 'efficiency';
  requestId?: number;
}

type State = 'idle' | 'loading' | 'done' | 'error';

export default function CycleDataGraph({ mode, requestId }: Props) {
  const [cycles, setCycles] = useState<FilteredCycle[]>([]);
  const [state, setState]   = useState<State>('idle');
  const [error, setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    let pollId: ReturnType<typeof setInterval> | null = null;
    setState('loading');
    setError(null);

    async function load() {
      try {
        let id = requestId;

        if (id == null) {
          // Auto-trigger an all-time filter request
          const nowIso   = new Date().toISOString();
          const epochIso = '2000-01-01T00:00:00.000Z';
          id = await submitFilterRequest({
            filterStart: epochIso,
            filterEnd:   nowIso,
            filterBy:    'time',
            periodLabel: null,
          });

          // Poll for done
          await new Promise<void>((resolve, reject) => {
            pollId = setInterval(async () => {
              try {
                const status = await pollFilterStatus(id!);
                if (status.status === 'done') {
                  if (pollId) clearInterval(pollId);
                  resolve();
                } else if (status.status === 'error') {
                  if (pollId) clearInterval(pollId);
                  reject(new Error('Backend reported error for all-time request'));
                }
              } catch (e) {
                if (pollId) clearInterval(pollId);
                reject(e);
              }
            }, 2500);
          });
        }

        const data = await fetchFilterCycles(id);
        if (!active) return;
        setCycles(data);
        setState('done');
      } catch (e) {
        if (!active) return;
        setError((e as Error).message);
        setState('error');
      }
    }

    load();
    return () => {
      active = false;
      if (pollId) clearInterval(pollId);
    };
  }, [requestId, mode]);

  if (state === 'loading' || state === 'idle')
    return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (state === 'error')   return <Alert severity="error">{error}</Alert>;
  if (!cycles.length)      return <Typography color="text.secondary">No cycle data available.</Typography>;

  // Show last 100 cycles to keep chart readable
  const visible = cycles.slice(-100);

  if (mode === 'energy') {
    const chartData = visible.map(c => ({ cycle: c.cycleNumber, kWh: parseFloat(c.energyKwh.toFixed(3)) }));
    return (
      <Box sx={{ width: '100%', height: 320 }}>
        <ResponsiveContainer>
          <BarChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 8 }}>
            <CartesianGrid strokeDasharray="3 3" />
            <XAxis dataKey="cycle" tick={{ fontSize: 10 }} label={{ value: 'Cycle #', position: 'insideBottom', offset: -4 }} />
            <YAxis unit=" kWh" tick={{ fontSize: 11 }} />
            <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(3)} kWh`, 'Energy']} />
            <Bar dataKey="kWh" fill="#1565c0" radius={[2, 2, 0, 0]} name="Energy (kWh)" />
          </BarChart>
        </ResponsiveContainer>
      </Box>
    );
  }

  // efficiency mode
  const chartData = visible.map(c => ({
    cycle: c.cycleNumber,
    kwPerKg: c.productionKg > 0 ? parseFloat((c.energyKwh / c.productionKg).toFixed(4)) : 0,
  }));
  return (
    <Box sx={{ width: '100%', height: 320 }}>
      <ResponsiveContainer>
        <LineChart data={chartData} margin={{ top: 8, right: 16, left: 0, bottom: 8 }}>
          <CartesianGrid strokeDasharray="3 3" />
          <XAxis dataKey="cycle" tick={{ fontSize: 10 }} label={{ value: 'Cycle #', position: 'insideBottom', offset: -4 }} />
          <YAxis unit=" kWh/kg" tick={{ fontSize: 11 }} />
          <Tooltip formatter={(v: number | undefined) => [`${(v ?? 0).toFixed(4)} kWh/kg`, 'Efficiency']} />
          <Line type="monotone" dataKey="kwPerKg" stroke="#e65100" dot={false} strokeWidth={2} name="kWh/kg" />
        </LineChart>
      </ResponsiveContainer>
    </Box>
  );
}
