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
import TrendMetricGraph from './TrendMetricGraph';
import { byParamOrder } from '../utils/unitConverters';
import type { LifetimeParameter, ShotsBreakdownEntry } from '../types';

const POLL_INTERVAL_MS = 60_000;

// Section 1 graphs cover the machine's entire recorded life — no rolling window.
//
// They are served from the plc_daily_trends rollup rather than raw history, which is what makes
// "all-time" affordable: one row per day instead of ~1 M raw rows per year for the utility tags
// alone. Passing no bounds is what selects the all-time series; the server picks the granularity
// from the span of history that exists and gap-fills every bucket in between.
//
// blast_time_sec and cycle_count ARE plotted now. plc_daily_trends has carried blast_on_sec and
// cycle_count all along — the claim that it had no column to plot them from was simply wrong, and
// the two tiles sat scalar-only for no reason.
//
// energy_per_casting_kwh_kg is deliberately NOT graphed, in either section. Lifetime kWh/kg drifts
// by thousandths across a bucket, so any axis fitted to it magnifies rounding into a trend line
// that invites conclusions the data does not support. The tile carries the number.
//
// No `title` here: the dialog inherits the tile's own name. These charts open from a Section 1
// tile and Section 1 IS the lifetime block, so an "All-Time <name>" title restated the tile and
// the block it sits in at once. `info` is the formula, behind the info icon beside that title.
const GRAPHABLE: Record<string, { info: string; render: () => React.ReactNode }> = {
  machine_utility_pct: {
    info: 'Blast time as a percentage of machine on-time',
    render: () => <UtilityGraph />,
  },
  production_qty_kg: {
    info: 'Daily tonnage with running total',
    render: () => <ProductionGraph />,
  },
  energy_kwh_total: {
    info: 'Average impeller current × cycle duration, summed per day',
    render: () => <TrendMetricGraph metric="energy" />,
  },
  blast_time_sec: {
    info: 'Duration the blast was ON',
    render: () => <TrendMetricGraph metric="blastTime" />,
  },
  cycle_count: {
    info: 'Count of completed blast cycles',
    render: () => <TrendMetricGraph metric="cycleCount" />,
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
  /** Optional second line under the title. Most blocks need none: the title says it. */
  subtitle?: string;
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
          {subtitle && (
            <Typography variant="caption" color="text.secondary" display="block">
              {subtitle}
            </Typography>
          )}
        </Box>
        <Box display="flex" alignItems="center" gap={0.5}>
          {lastFetched && (
            <Typography variant="caption" color="text.secondary">
              Last updated {lastFetched.toLocaleTimeString()}
            </Typography>
          )}
          <Tooltip title="Refresh now">
            <span>
              <IconButton onClick={load} size="small" disabled={loading}>
                <RefreshIcon fontSize="small" />
              </IconButton>
            </span>
          </Tooltip>
        </Box>
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
                graphInfo={graphDef?.info}
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
            Cycles completed between consecutive refills.
          </Typography>
          <ShotsBreakdownChart data={shotsData} />
        </>
      )}
    </Box>
  );
};
