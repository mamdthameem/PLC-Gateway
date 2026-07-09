import { useState, useEffect } from 'react';
import {
  Box, Typography, CircularProgress, Alert, Divider,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Paper,
} from '@mui/material';
import { fetchFilterResults, fetchFilterCycles, fetchFilterShots } from '../services/filterService';
import ExpandableMetricCard from './ExpandableMetricCard';
import ShotsBreakdownChart from './ShotsBreakdownChart';
import UtilityGraph from './UtilityGraph';
import ProductionGraph from './ProductionGraph';
import CycleDataGraph from './CycleDataGraph';
import type { FilterResult, FilteredCycle, ShotsBreakdownEntry } from '../types';

interface Props {
  requestId: number;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
  label: string;
}

const GRAPHABLE: Record<string, string> = {
  machine_utility_pct:       'Hourly Utility Trend',
  production_qty_kg:         'Production Over Time',
  energy_kwh_total:          'Energy per Cycle',
  energy_per_casting_kwh_kg: 'Efficiency per Cycle (kWh/kg)',
};

function CycleTable({ cycles }: { cycles: FilteredCycle[] }) {
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell>Cycle #</TableCell>
            <TableCell>Start</TableCell>
            <TableCell>End</TableCell>
            <TableCell>Metal 1</TableCell>
            <TableCell>Metal 2</TableCell>
            <TableCell>Metal 3</TableCell>
            <TableCell>Metal 4</TableCell>
            <TableCell align="right">Production (kg)</TableCell>
            <TableCell align="right">Energy (kWh)</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {cycles.map(c => (
            <TableRow key={c.cycleNumber}>
              <TableCell>{c.cycleNumber}</TableCell>
              <TableCell>{new Date(c.blastStart).toLocaleString()}</TableCell>
              <TableCell>{new Date(c.blastEnd).toLocaleString()}</TableCell>
              <TableCell>{c.metal1Name ? `${c.metal1Name} ${c.metal1WeightKg?.toFixed(1)} kg` : '—'}</TableCell>
              <TableCell>{c.metal2Name ? `${c.metal2Name} ${c.metal2WeightKg?.toFixed(1)} kg` : '—'}</TableCell>
              <TableCell>{c.metal3Name ? `${c.metal3Name} ${c.metal3WeightKg?.toFixed(1)} kg` : '—'}</TableCell>
              <TableCell>{c.metal4Name ? `${c.metal4Name} ${c.metal4WeightKg?.toFixed(1)} kg` : '—'}</TableCell>
              <TableCell align="right">{c.productionKg.toFixed(2)}</TableCell>
              <TableCell align="right">{c.energyKwh.toFixed(3)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

export default function FilterResultsView({ requestId, filterStart, filterEnd, filterBy, label }: Props) {
  const [results, setResults] = useState<FilterResult[]>([]);
  const [cycles,  setCycles]  = useState<FilteredCycle[]>([]);
  const [shots,   setShots]   = useState<ShotsBreakdownEntry[]>([]);
  const [loading, setLoading] = useState(true);
  const [error,   setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);

    Promise.all([
      fetchFilterResults(requestId),
      fetchFilterCycles(requestId),
      fetchFilterShots(requestId),
    ])
      .then(([r, c, s]) => {
        if (!active) return;
        setResults(r);
        setCycles(c);
        setShots(s);
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [requestId]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 6 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!results.length) return <Alert severity="info">No results found for the selected filter.</Alert>;

  function renderGraph(paramName: string) {
    switch (paramName) {
      case 'machine_utility_pct':
        return () => <UtilityGraph windowStart={filterStart} windowEnd={filterEnd} />;
      case 'production_qty_kg':
        return () => <ProductionGraph windowStart={filterStart} windowEnd={filterEnd} />;
      case 'energy_kwh_total':
        return () => <CycleDataGraph requestId={requestId} mode="energy" />;
      case 'energy_per_casting_kwh_kg':
        return () => <CycleDataGraph requestId={requestId} mode="efficiency" />;
      default:
        return undefined;
    }
  }

  return (
    <Box>
      <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem', mb: 0.5 }}>
        Filtered Parameters — {label}
      </Typography>
      {filterBy === 'time' && (
        <Typography variant="caption" color="text.secondary" display="block" mb={2}>
          {new Date(filterStart).toLocaleString()} → {new Date(filterEnd).toLocaleString()}
        </Typography>
      )}

      {/* Scalar parameter grid — production and last_refill are not shown in filtered view */}
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', sm: 'repeat(2,1fr)', md: 'repeat(3,1fr)', lg: 'repeat(4,1fr)' },
          gap: 2,
          mb: 3,
        }}
      >
        {results
          .filter(r => r.parameterName !== 'production_qty_kg' && r.parameterName !== 'last_refill_epoch_sec')
          .map(r => (
          <ExpandableMetricCard
            key={r.parameterName}
            parameterName={r.parameterName}
            value={r.value}
            graphTitle={GRAPHABLE[r.parameterName]}
            renderGraph={renderGraph(r.parameterName)}
          />
        ))}
      </Box>

      {/* Cycle table — always visible */}
      {cycles.length > 0 && (
        <>
          <Divider sx={{ mb: 2 }} />
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 1 }}>
            Cycle Breakdown ({cycles.length} cycles)
          </Typography>
          <CycleTable cycles={cycles} />
        </>
      )}

      {/* Filtered shots chart */}
      {shots.length > 0 && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 1 }}>
            Blast Cycles per Refill Interval
          </Typography>
          <ShotsBreakdownChart data={shots} />
        </>
      )}
    </Box>
  );
}
