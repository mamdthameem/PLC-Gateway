import React, { useEffect, useState, useCallback } from 'react';
import {
  Box, Typography, CircularProgress, Alert, IconButton, Tooltip, Divider,
} from '@mui/material';
import RefreshIcon from '@mui/icons-material/Refresh';
import { fetchLifetimeParameters } from '../services/lifetimeService';
import { fetchShotsBreakdown } from '../services/shotsBreakdownService';
import ExpandableMetricCard from './ExpandableMetricCard';
import ShotsBreakdownChart from './ShotsBreakdownChart';
import UtilityGraph from './UtilityGraph';
import ProductionGraph from './ProductionGraph';
import EnergyTrendGraph from './EnergyTrendGraph';
import { byParamOrder } from '../utils/unitConverters';
import type { LifetimeParameter, ShotsBreakdownEntry } from '../types';

const POLL_INTERVAL_MS = 60_000;

// Section 1 graphs cover the machine's entire recorded life — no rolling window.
//
// They are served from the plc_daily_trends rollup rather than raw history, which is what makes
// "all-time" affordable: one row per day instead of ~1 M raw rows per year for the utility tags
// alone. Passing no bounds is what selects the all-time series.
const GRAPHABLE: Record<string, { title: string; render: () => React.ReactNode }> = {
  machine_utility_pct: {
    title: 'Machine Utility — all-time',
    render: () => <UtilityGraph />,
  },
  production_qty_kg: {
    title: 'Production — all-time',
    render: () => <ProductionGraph />,
  },
  energy_kwh_total: {
    title: 'Energy — all-time',
    render: () => <EnergyTrendGraph mode="energy" />,
  },
  energy_per_casting_kwh_kg: {
    title: 'Energy per Casting — all-time (kWh/kg)',
    render: () => <EnergyTrendGraph mode="efficiency" />,
  },
};

export const LifetimeSection: React.FC = () => {
  const [params, setParams]           = useState<LifetimeParameter[]>([]);
  const [shotsData, setShotsData]     = useState<ShotsBreakdownEntry[]>([]);
  const [loading, setLoading]         = useState(true);
  const [error, setError]             = useState<string | null>(null);
  const [lastFetched, setLastFetched] = useState<Date | null>(null);

  const load = useCallback(async () => {
    try {
      const [paramData, shots] = await Promise.all([
        fetchLifetimeParameters(),
        fetchShotsBreakdown(),
      ]);
      setParams(paramData);
      setShotsData(shots);
      setLastFetched(new Date());
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load data');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    load();
    const timer = setInterval(load, POLL_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [load]);

  // machine_status is excluded here — MachineStatusTile renders it above this section, where it
  // can also report the PLC link state.
  const displayParams = params
    .filter(p => p.parameterName !== 'machine_status')
    .sort(byParamOrder);

  return (
    <Box>
      <Box display="flex" alignItems="center" justifyContent="space-between" mb={2}>
        <Box>
          <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem' }}>
            Lifetime Parameters
          </Typography>
          <Typography variant="caption" color="text.secondary">
            Cumulative since commissioning · refreshes every 60 s
            {lastFetched && ` · last updated ${lastFetched.toLocaleTimeString()}`}
          </Typography>
        </Box>
        <Tooltip title="Refresh now">
          <span>
            <IconButton onClick={load} size="small" disabled={loading}>
              <RefreshIcon fontSize="small" />
            </IconButton>
          </span>
        </Tooltip>
      </Box>

      {loading && displayParams.length === 0 && (
        <Box display="flex" justifyContent="center" py={4}><CircularProgress size={28} /></Box>
      )}

      {error && <Alert severity="error" sx={{ mb: 2 }}>{error}</Alert>}

      {displayParams.length > 0 && (
        <Box
          sx={{
            display: 'grid',
            gridTemplateColumns: { xs: '1fr', sm: 'repeat(2,1fr)', md: 'repeat(3,1fr)', lg: 'repeat(4,1fr)' },
            gap: 2,
            mb: 3,
          }}
        >
          {displayParams.map(p => {
            const graphDef = GRAPHABLE[p.parameterName];
            return (
              <ExpandableMetricCard
                key={p.parameterName}
                parameterName={p.parameterName}
                value={p.value}
                updatedAt={p.updatedAt}
                graphTitle={graphDef?.title}
                renderGraph={graphDef ? graphDef.render : undefined}
              />
            );
          })}
        </Box>
      )}

      {shotsData.length > 0 && (
        <>
          <Divider sx={{ mb: 2 }} />
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 0.5 }}>
            Blast Cycles per Refill Interval
          </Typography>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            Blast cycles run between each shot refill and the next — the last bar is the current
            interval.
          </Typography>
          <ShotsBreakdownChart data={shotsData} />
        </>
      )}
    </Box>
  );
};
