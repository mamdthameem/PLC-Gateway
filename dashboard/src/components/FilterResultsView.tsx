import { useState, useEffect } from 'react';
import {
  Box, Typography, CircularProgress, Alert, Divider, Button, Chip,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Paper,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import {
  fetchFilterResults, fetchFilterCycles, fetchFilterShots, fetchFilterMetals,
} from '../services/filterService';
import ExpandableMetricCard from './ExpandableMetricCard';
import ShotsBreakdownChart from './ShotsBreakdownChart';
import UtilityGraph from './UtilityGraph';
import CycleDataGraph from './CycleDataGraph';
import { PARAM_META, formatParameterValue, byParamOrder } from '../utils/unitConverters';
import { exportFilteredWorkbook } from '../utils/exportFilteredExcel';
import type {
  FilterResult, FilteredCycle, FilteredMetalProduction, ShotsBreakdownEntry,
} from '../types';

interface Props {
  requestId: number;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
  label: string;
}

function SectionHeading({ title, note }: { title: string; note?: string }) {
  return (
    <>
      <Typography variant="subtitle2" fontWeight={600} sx={{ mb: note ? 0.25 : 1 }}>
        {title}
      </Typography>
      {note && (
        <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
          {note}
        </Typography>
      )}
    </>
  );
}

function ParameterTable({ results }: { results: FilterResult[] }) {
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Parameter</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Value</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {results.map(r => (
            <TableRow key={r.parameterName}>
              <TableCell>{PARAM_META[r.parameterName]?.label ?? r.parameterName}</TableCell>
              <TableCell align="right">{formatParameterValue(r.parameterName, r.value)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

function MetalProductionTable({ metals }: { metals: FilteredMetalProduction[] }) {
  const total = metals.reduce((sum, m) => sum + m.productionKg, 0);
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Casting Metal</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Declared Weight (kg)</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {metals.map(m => (
            <TableRow key={m.metalName}>
              <TableCell>
                {m.metalName === 'unspecified'
                  ? <Chip label="unspecified" size="small" variant="outlined" />
                  : m.metalName}
              </TableCell>
              <TableCell align="right">
                {m.productionKg.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
              </TableCell>
            </TableRow>
          ))}
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Total</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>
              {total.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
            </TableCell>
          </TableRow>
        </TableBody>
      </Table>
    </TableContainer>
  );
}

function CycleTable({ cycles }: { cycles: FilteredCycle[] }) {
  const metalCell = (name: string | null, kg: number | null) =>
    name ? `${name}${kg != null ? ` · ${kg.toFixed(1)} kg` : ''}` : '—';

  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto', maxHeight: 520 }}>
      <Table size="small" stickyHeader>
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Cycle #</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Start</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>End</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Metal 1</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Metal 2</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Metal 3</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Metal 4</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Production (kg)</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Energy (kWh)</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Shots Usage</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {cycles.map(c => (
            <TableRow key={c.cycleNumber}>
              <TableCell>{c.cycleNumber}</TableCell>
              <TableCell>{new Date(c.blastStart).toLocaleString()}</TableCell>
              <TableCell>{new Date(c.blastEnd).toLocaleString()}</TableCell>
              <TableCell>{metalCell(c.metal1Name, c.metal1WeightKg)}</TableCell>
              <TableCell>{metalCell(c.metal2Name, c.metal2WeightKg)}</TableCell>
              <TableCell>{metalCell(c.metal3Name, c.metal3WeightKg)}</TableCell>
              <TableCell>{metalCell(c.metal4Name, c.metal4WeightKg)}</TableCell>
              <TableCell align="right">{c.productionKg.toFixed(2)}</TableCell>
              <TableCell align="right">{c.energyKwh.toFixed(3)}</TableCell>
              <TableCell align="right">{c.shotsUsage.toFixed(4)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

function ShotsTable({ shots }: { shots: ShotsBreakdownEntry[] }) {
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto', maxHeight: 400 }}>
      <Table size="small" stickyHeader>
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Refill Timestamp</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Blast Cycles Until Next Refill</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {shots.map(s => (
            <TableRow key={s.refillTimestamp}>
              <TableCell>{new Date(s.refillTimestamp).toLocaleString()}</TableCell>
              <TableCell align="right">{s.blastCount}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

// Only the two cycle-derived graphs work for every filter mode, because they key off the request
// itself. Utility is time-derived, so it is offered for time filters only — a cycle or metal
// filter carries no meaningful start/end (both are set to NOW() as a placeholder, since the
// columns are NOT NULL), which is why those modes get tables rather than a time-axis chart.
const GRAPHABLE_ALL_MODES: Record<string, string> = {
  energy_kwh_total:          'Energy per Cycle',
  energy_per_casting_kwh_kg: 'Efficiency per Cycle (kWh/kg)',
};
const GRAPHABLE_TIME_ONLY: Record<string, string> = {
  machine_utility_pct: 'Utility Trend',
};

export default function FilterResultsView({ requestId, filterStart, filterEnd, filterBy, label }: Props) {
  const [results, setResults] = useState<FilterResult[]>([]);
  const [cycles,  setCycles]  = useState<FilteredCycle[]>([]);
  const [shots,   setShots]   = useState<ShotsBreakdownEntry[]>([]);
  const [metals,  setMetals]  = useState<FilteredMetalProduction[]>([]);
  const [loading, setLoading] = useState(true);
  const [error,   setError]   = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);

    Promise.all([
      fetchFilterResults(requestId),
      fetchFilterCycles(requestId),
      fetchFilterShots(requestId),
      fetchFilterMetals(requestId),
    ])
      .then(([r, c, s, m]) => {
        if (!active) return;
        setResults([...r].sort(byParamOrder));
        setCycles(c);
        setShots(s);
        setMetals(m);
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

  const isTimeFilter = filterBy === 'time';

  function graphTitle(paramName: string): string | undefined {
    return GRAPHABLE_ALL_MODES[paramName]
      ?? (isTimeFilter ? GRAPHABLE_TIME_ONLY[paramName] : undefined);
  }

  function renderGraph(paramName: string): (() => React.ReactNode) | undefined {
    switch (paramName) {
      case 'machine_utility_pct':
        return isTimeFilter
          ? () => <UtilityGraph windowStart={filterStart} windowEnd={filterEnd} />
          : undefined;
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
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" flexWrap="wrap" gap={1} mb={2}>
        <Box>
          <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem', mb: 0.5 }}>
            Filtered Parameters — {label}
          </Typography>
          {isTimeFilter && (
            <Typography variant="caption" color="text.secondary" display="block">
              {new Date(filterStart).toLocaleString()} → {new Date(filterEnd).toLocaleString()}
            </Typography>
          )}
        </Box>
        <Button
          variant="outlined"
          size="small"
          startIcon={<DownloadIcon />}
          onClick={() => exportFilteredWorkbook({
            label, filterBy, filterStart, filterEnd, results, cycles, metals, shots,
          })}
        >
          Download Excel
        </Button>
      </Box>

      {/* Scalar cards — same canonical order as Section 1 */}
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', sm: 'repeat(2,1fr)', md: 'repeat(3,1fr)', lg: 'repeat(4,1fr)' },
          gap: 2,
          mb: 3,
        }}
      >
        {results.map(r => (
          <ExpandableMetricCard
            key={r.parameterName}
            parameterName={r.parameterName}
            value={r.value}
            graphTitle={graphTitle(r.parameterName)}
            renderGraph={renderGraph(r.parameterName)}
          />
        ))}
      </Box>

      <Divider sx={{ mb: 2 }} />
      <SectionHeading title="Parameters" note="The same values as the cards above, in table form for export." />
      <ParameterTable results={results} />

      <Divider sx={{ mt: 3, mb: 2 }} />
      <SectionHeading
        title="Production by Casting Metal"
        note="Summed declared casting-metal weights for this filter. Section 1 reports production from the PLC's Tonnage accumulator instead, so the two figures answer different questions and need not match."
      />
      {metals.length > 0
        ? <MetalProductionTable metals={metals} />
        : <Alert severity="info">No casting metal weights were declared for the cycles in this filter.</Alert>}

      <Divider sx={{ mt: 3, mb: 2 }} />
      <SectionHeading title={`Cycle Breakdown (${cycles.length} ${cycles.length === 1 ? 'cycle' : 'cycles'})`} />
      {cycles.length > 0
        ? <CycleTable cycles={cycles} />
        : <Alert severity="info">No completed blast cycles fall within this filter.</Alert>}

      <Divider sx={{ mt: 3, mb: 2 }} />
      <SectionHeading title="Blast Cycles per Refill Interval" />
      {shots.length > 0 ? (
        <>
          <ShotsBreakdownChart data={shots} />
          <Box sx={{ mt: 2 }}>
            <ShotsTable shots={shots} />
          </Box>
        </>
      ) : (
        <Alert severity="info">No shot refills were recorded in this filter.</Alert>
      )}
    </Box>
  );
}
