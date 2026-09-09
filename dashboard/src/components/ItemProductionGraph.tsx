import { useState, useEffect } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import {
  BarChart, Bar, XAxis, YAxis, CartesianGrid, Tooltip, ResponsiveContainer, Cell,
} from 'recharts';
import { fetchFilterMetals } from '../services/filterService';
import type { FilteredMetalProduction } from '../types';

interface Props {
  requestId: number;
}

type State = 'loading' | 'done' | 'error';

/**
 * Section 2 production split per casting item — one bar per declared item name.
 *
 * This replaced the "Production by Casting Metal" table. The numbers are identical; only the
 * presentation changed. The tile it opens from shows the total of these bars.
 *
 * Under an item filter this is a single bar by design: the backend scopes the declared-weight sum
 * to the filtered item, so another item declared in the same cycle does not land in this item's
 * production (see README, "Item filter scoping").
 *
 * The API route is still /metals and the field is still metalName — the PLC tags and DB columns
 * say metal; only the wording shown to the user says item.
 */
export default function ItemProductionGraph({ requestId }: Props) {
  const [items, setItems] = useState<FilteredMetalProduction[]>([]);
  const [state, setState] = useState<State>('loading');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setState('loading');
    setError(null);

    fetchFilterMetals(requestId)
      .then(data => {
        if (!active) return;
        setItems(data);
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
  if (!items.length) {
    return (
      <Typography color="text.secondary">
        No casting item weights were declared for the cycles in this filter.
      </Typography>
    );
  }

  const total = items.reduce((sum, i) => sum + i.productionKg, 0);

  // A weight declared against a blank name is recorded as 'unspecified' rather than dropped or
  // guessed at — call that out instead of letting it read as a real item name.
  const data = items.map(i => ({
    name: i.metalName,
    productionKg: i.productionKg,
    unspecified: i.metalName === 'unspecified',
  }));

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
        Declared casting weight per item · total{' '}
        {total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} kg
      </Typography>

      <Box sx={{ width: '100%', height: 340 }}>
        <ResponsiveContainer>
          <BarChart data={data} margin={{ top: 8, right: 16, left: 8, bottom: 48 }}>
            <CartesianGrid strokeDasharray="3 3" vertical={false} />
            <XAxis
              dataKey="name"
              angle={-30}
              textAnchor="end"
              interval={0}
              height={60}
              tick={{ fontSize: 12 }}
            />
            <YAxis
              tick={{ fontSize: 12 }}
              label={{ value: 'kg', angle: -90, position: 'insideLeft', style: { fontSize: 12 } }}
            />
            <Tooltip
              formatter={(v: number | undefined) => [
                `${(v ?? 0).toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })} kg`,
                'Declared weight',
              ]}
            />
            <Bar dataKey="productionKg" radius={[4, 4, 0, 0]}>
              {data.map(d => (
                <Cell key={d.name} fill={d.unspecified ? '#94a3b8' : '#2563eb'} />
              ))}
            </Bar>
          </BarChart>
        </ResponsiveContainer>
      </Box>
    </Box>
  );
}
