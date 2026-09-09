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
//
// blast_time_sec and cycle_count are absent on purpose: plc_daily_trends has no column to plot
// either from. They are scalar-only in Section 2 as well, so the two sections match.
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

interface Props {
  /**
   * Which parameter names to render. The page shows this component twice with disjoint lists:
   * the Section 1-only parameters above the filter bar, and the ones shared with Section 2 below
   * it — see SECTION1_ONLY_PARAM_KEYS / SHARED_PARAM_KEYS.
   */
  include: readonly string[];
  title: string;
  subtitle: string;
  /** The shots-per-refill chart is a Section 1-only output, so only the upper block asks for it. */
  showShotsChart?: boolean;
}

/**
 * A grid of Section 1 (all-time) parameter tiles, polled every 60 s.
 *
 * Both instances read the same `/api/lifetime` endpoint independently. That is deliberate — they
 * are both Section 1, so there is no cross-section coupling to worry about, and the endpoint
 * returns ten rows. Section 2 shares nothing with either of them.
 */
export const LifetimeSection: React.FC<Props> = ({ include, title, subtitle, showShotsChart = false }) => {
  const [params, setParams]           = useState<LifetimeParameter[]>([]);
  const [shotsData, setShotsData]     = useState<ShotsBreakdownEntry[]>([]);
  const [loading, setLoading]         = useState(true);
  const [error, setError]             = useState<string | null>(null);
  const [lastFetched, setLastFetched] = useState<Date | null>(null);

  const load = useCallback(async () => {
    try {
      const [paramData, shots] = await Promise.all([
        fetchLifetimeParameters(),
        showShotsChart ? fetchShotsBreakdown() : Promise.resolve([] as ShotsBreakdownEntry[]),
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
  }, [showShotsChart]);

  useEffect(() => {
    load();
    const timer = setInterval(load, POLL_INTERVAL_MS);
    return () => clearInterval(timer);
  }, [load]);

  // machine_status is excluded even when it is in `include` — MachineStatusTile renders it above
  // this section, where it can also report the PLC link state.
  const displayParams = params
    .filter(p => include.includes(p.parameterName) && p.parameterName !== 'machine_status')
    .sort(byParamOrder);

  return (
    <Box>
      <Box display="flex" alignItems="center" justifyContent="space-between" mb={2}>
        <Box>
          <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem' }}>
            {title}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {subtitle}
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
            mb: showShotsChart ? 3 : 0,
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

      {showShotsChart && shotsData.length > 0 && (
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
