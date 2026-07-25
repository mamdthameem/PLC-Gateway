import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  BarChart, Bar, LineChart, Line, XAxis, YAxis, CartesianGrid,
  Tooltip, ResponsiveContainer,
} from 'recharts';
import { fetchFilterCycles } from '../services/filterService';
import type { FilteredCycle } from '../types';

interface Props {
  mode: 'energy' | 'efficiency';
  /**
   * Section 2 only — the completed filter request whose cycles are charted. Section 1 uses
   * EnergyTrendGraph instead: it reads the daily rollup rather than submitting a filter request,
   * so opening a Section 1 graph no longer writes a row to calculation_requests.
   */
  requestId: number;
}

type State = 'loading' | 'done' | 'error';

/** Per-cycle energy / efficiency within one filtered window. */
export default function CycleDataGraph({ mode, requestId }: Props) {
  const [cycles, setCycles] = useState<FilteredCycle[]>([]);
  const [state, setState]   = useState<State>('loading');
  const [error, setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setState('loading');
    setError(null);

    fetchFilterCycles(requestId)
      .then(data => {
        if (!active) return;
        setCycles(data);
        setState('done');
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setState('error');
      });

    return () => { active = false; };
  }, [requestId]);

  if (state === 'loading') return <Box sx={{ display: 'flex', justifyContent: 'center', py: 4 }}><CircularProgress /></Box>;
  if (state === 'error')   return <Alert severity="error">{error}</Alert>;
  if (!cycles.length)      return <Typography color="text.secondary">No cycle data in this filter.</Typography>;

  // Keep the chart readable when a filter spans a very large number of cycles.
  const visible = cycles.slice(-200);
  const truncated = cycles.length > visible.length;

  const note = truncated ? (
    <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 0.5 }}>
      Showing the most recent {visible.length} of {cycles.length} cycles — narrow the filter to see
      earlier ones.
    </Typography>
  ) : null;

  if (mode === 'energy') {
    const chartData = visible.map(c => ({ cycle: c.cycleNumber, kWh: parseFloat(c.energyKwh.toFixed(3)) }));
    return (
      <Box sx={{ width: '100%' }}>
        {note}
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
      </Box>
    );
  }

  const chartData = visible.map(c => ({
    cycle: c.cycleNumber,
    kwPerKg: c.productionKg > 0 ? parseFloat((c.energyKwh / c.productionKg).toFixed(4)) : 0,
  }));
  return (
    <Box sx={{ width: '100%' }}>
      {note}
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
    </Box>
  );
}
