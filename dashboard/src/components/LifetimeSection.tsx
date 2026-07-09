import React, { useEffect, useState, useCallback } from 'react';
import {
  Box, Typography, CircularProgress, Alert, IconButton, Tooltip, Divider, Paper,
} from '@mui/material';
import RefreshIcon from '@mui/icons-material/Refresh';
import { fetchLifetimeParameters } from '../services/lifetimeService';
import { fetchShotsBreakdown } from '../services/shotsBreakdownService';
import ExpandableMetricCard from './ExpandableMetricCard';
import ShotsBreakdownChart from './ShotsBreakdownChart';
import UtilityGraph from './UtilityGraph';
import ProductionGraph from './ProductionGraph';
import CycleDataGraph from './CycleDataGraph';
import type { LifetimeParameter, ShotsBreakdownEntry } from '../types';

const POLL_INTERVAL_MS = 60_000;

// Parameters shown in both sections — get graph dialogs in Section 1
const GRAPHABLE: Record<string, { title: string; renderFn: (now: Date) => () => React.ReactNode }> = {
  machine_utility_pct: {
    title: 'Daily Utility (last 30 days)',
    renderFn: (now) => {
      const start = new Date(now.getTime() - 30 * 24 * 3_600_000).toISOString();
      const end   = now.toISOString();
      return () => <UtilityGraph windowStart={start} windowEnd={end} />;
    },
  },
  production_qty_kg: {
    title: 'Production Over Time (last 7 days)',
    renderFn: (now) => {
      const start = new Date(now.getTime() - 7 * 24 * 3_600_000).toISOString();
      const end   = now.toISOString();
      return () => <ProductionGraph windowStart={start} windowEnd={end} />;
    },
  },
  energy_kwh_total: {
    title: 'Energy per Cycle (all-time)',
    renderFn: () => () => <CycleDataGraph mode="energy" />,
  },
  energy_per_casting_kwh_kg: {
    title: 'Efficiency per Cycle — kWh/kg (all-time)',
    renderFn: () => () => <CycleDataGraph mode="efficiency" />,
  },
};

function ShotsUsageTile({ shotsData }: { shotsData: ShotsBreakdownEntry[] }) {
  const latest = shotsData.length > 0 ? shotsData[shotsData.length - 1] : null;
  return (
    <Paper sx={{ p: 2.5, borderRadius: 2, display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <Typography
        variant="caption"
        sx={{ color: 'text.secondary', fontWeight: 600, letterSpacing: '0.07em', fontSize: '0.68rem', textTransform: 'uppercase' }}
      >
        Effective Shots Usage
      </Typography>
      <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1.3rem', lineHeight: 1.2, mt: 0.5 }}>
        {latest !== null ? `${latest.blastCount} ${latest.blastCount === 1 ? 'cycle' : 'cycles'}` : '—'}
      </Typography>
      {latest && (
        <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', mt: 'auto' }}>
          Refilled {new Date(latest.refillTimestamp).toLocaleString()}
        </Typography>
      )}
    </Paper>
  );
}

export const LifetimeSection: React.FC = () => {
  const [params, setParams]     = useState<LifetimeParameter[]>([]);
  const [shotsData, setShotsData] = useState<ShotsBreakdownEntry[]>([]);
  const [loading, setLoading]   = useState(true);
  const [error, setError]       = useState<string | null>(null);
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

  const now = new Date();

  // Exclude machine_status (shown separately by MachineStatusTile)
  const displayParams = params.filter(p => p.parameterName !== 'machine_status');

  // If avg_shot_refill_time_sec is not in the DB response, compute from shots data.
  // Always render the tile regardless — show '—' when there isn't enough data yet.
  const hasAvgRefill = displayParams.some(p => p.parameterName === 'avg_shot_refill_time_sec');
  const computedAvgRefillValue: string = (() => {
    if (hasAvgRefill) return '';          // DB row present — tile rendered via displayParams below
    if (shotsData.length < 2) return 'NaN'; // not enough data — formatParameterValue shows '—'
    let total = 0;
    let count = 0;
    for (let i = 1; i < shotsData.length; i++) {
      const diff = (new Date(shotsData[i].refillTimestamp).getTime()
                  - new Date(shotsData[i - 1].refillTimestamp).getTime()) / 1000;
      if (diff > 0) { total += diff; count++; }
    }
    return count > 0 ? String(total / count) : 'NaN';
  })();

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

      {(displayParams.length > 0 || shotsData.length > 0 || !hasAvgRefill) && (
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
                renderGraph={graphDef ? graphDef.renderFn(now) : undefined}
              />
            );
          })}
          {/* Shots usage tile (Section 1 only) */}
          {shotsData.length > 0 && <ShotsUsageTile shotsData={shotsData} />}

          {/* Avg Shot Refill Time — fallback tile when not stored in plc_lifetime_parameters */}
          {!hasAvgRefill && (
            <ExpandableMetricCard
              parameterName="avg_shot_refill_time_sec"
              value={computedAvgRefillValue}
            />
          )}
        </Box>
      )}

      {shotsData.length > 0 && (
        <>
          <Divider sx={{ mb: 2 }} />
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 1 }}>
            Blast Cycles per Refill Interval
          </Typography>
          <ShotsBreakdownChart data={shotsData} />
        </>
      )}
    </Box>
  );
};
