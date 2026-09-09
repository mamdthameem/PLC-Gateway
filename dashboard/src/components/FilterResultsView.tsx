import { useState, useEffect } from 'react';
import {
  Box, Typography, CircularProgress, Alert, Divider, Button, Chip,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Paper,
} from '@mui/material';
import DownloadIcon from '@mui/icons-material/Download';
import { fetchFilterResults, fetchFilterCycles, fetchFilterMetals } from '../services/filterService';
import ExpandableMetricCard from './ExpandableMetricCard';
import UtilityGraph from './UtilityGraph';
import CycleDataGraph from './CycleDataGraph';
import TrendMetricGraph from './TrendMetricGraph';
import ItemProductionGraph from './ItemProductionGraph';
import FilteredAmpsPanel from './FilteredAmpsPanel';
import { byParamOrder } from '../utils/unitConverters';
import { exportFilteredWorkbook } from '../utils/exportFilteredExcel';
import type {
  FilterResult, FilteredCycle, FilteredMetalProduction, Section2ParamKey,
} from '../types';

interface Props {
  requestId: number;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
  /** Set only for an item filter — shown in the header so the scope is unmistakable. */
  itemName: string | null;
  label: string;
  /** Empty means the request predates the toggles and computed everything. */
  selectedParameters: Section2ParamKey[];
}

function SectionHeading({ title, count }: { title: string; count?: string }) {
  return (
    <Box display="flex" alignItems="center" gap={1} sx={{ mb: 1 }}>
      <Typography variant="subtitle2" fontWeight={600}>
        {title}
      </Typography>
      {count && <Chip label={count} size="small" variant="outlined" sx={{ height: 18, fontSize: '0.68rem' }} />}
    </Box>
  );
}

/**
 * Production per declared casting item.
 *
 * This is DATA the tiles do not carry — the tiles show one total, this shows the split. It is not
 * a restatement of anything above it, which is why it stayed when the tile-duplicating "Parameters"
 * table was removed. The tile's tap-to-open graph plots these same rows.
 *
 * (Field is `metalName`: the DB column and API say metal, the UI says item — see README.)
 */
function ItemProductionTable({ items }: { items: FilteredMetalProduction[] }) {
  const total = items.reduce((sum, i) => sum + i.productionKg, 0);
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
      <Table size="small">
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Item</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Weight (kg)</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {items.map(i => (
            <TableRow key={i.metalName}>
              <TableCell>
                {i.metalName === 'unspecified'
                  ? <Chip label="unspecified" size="small" variant="outlined" />
                  : i.metalName}
              </TableCell>
              <TableCell align="right">
                {i.productionKg.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
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

/**
 * Per-cycle breakdown — timestamps, the four declared item slots, production and energy per cycle.
 *
 * Also data the tiles do not carry: the tiles show filter-wide totals, this shows what each
 * individual cycle contributed and which items it declared. Kept for the same reason.
 */
function CycleTable({ cycles }: { cycles: FilteredCycle[] }) {
  const itemCell = (name: string | null, kg: number | null) =>
    name ? `${name}${kg != null ? ` · ${kg.toFixed(1)} kg` : ''}` : '—';

  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto', maxHeight: 520 }}>
      <Table size="small" stickyHeader>
        <TableHead>
          <TableRow>
            <TableCell sx={{ fontWeight: 700 }}>Cycle No.</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Start Time</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>End Time</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 1</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 2</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 3</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 4</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Weight (kg)</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Energy (kWh)</TableCell>
          </TableRow>
        </TableHead>
        <TableBody>
          {cycles.map(c => (
            <TableRow key={c.cycleNumber}>
              <TableCell>{c.cycleNumber}</TableCell>
              <TableCell>{new Date(c.blastStart).toLocaleString()}</TableCell>
              <TableCell>{new Date(c.blastEnd).toLocaleString()}</TableCell>
              <TableCell>{itemCell(c.metal1Name, c.metal1WeightKg)}</TableCell>
              <TableCell>{itemCell(c.metal2Name, c.metal2WeightKg)}</TableCell>
              <TableCell>{itemCell(c.metal3Name, c.metal3WeightKg)}</TableCell>
              <TableCell>{itemCell(c.metal4Name, c.metal4WeightKg)}</TableCell>
              <TableCell align="right">{c.productionKg.toFixed(2)}</TableCell>
              <TableCell align="right">{c.energyKwh.toFixed(3)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

// Graph titles. Every graphable parameter opens its chart from its own tile.
//
// The split is about whether a chart needs a TIME axis. Cycle and item filters carry no meaningful
// one — filter_start/filter_end are NOT NULL, so both are set to NOW() as a placeholder — so a
// time-bucketed chart in those modes would plot a window that does not exist. Those tiles show the
// scalar only.
//
// energy_per_casting_kwh_kg has no chart in either section: per-cycle kWh/kg varies by thousandths
// across a filter, and an axis fitted to that turns rounding noise into an apparent trend. It was
// the one chart on the page that actively misinformed.
const GRAPHABLE_ALL_MODES: Record<string, string> = {
  production_qty_kg: 'Production per Casting Item',
  energy_kwh_total:  'Energy per Cycle',
};
const GRAPHABLE_TIME_ONLY: Record<string, string> = {
  machine_utility_pct: 'Machine Utility',
  blast_time_sec:      'Blast Time per Interval',
  cycle_count:         'Blast Cycles per Interval',
};

export default function FilterResultsView({
  requestId, filterStart, filterEnd, filterBy, itemName, label, selectedParameters,
}: Props) {
  const [results, setResults] = useState<FilterResult[]>([]);
  const [cycles,  setCycles]  = useState<FilteredCycle[]>([]);
  const [items,   setItems]   = useState<FilteredMetalProduction[]>([]);
  const [loading, setLoading] = useState(true);
  const [error,   setError]   = useState<string | null>(null);

  // These three fetches drive the tiles, the graphs, the two data tables AND the Excel export.
  // The workbook is built from this data, never from a rendered table, so table changes never
  // break the export.
  useEffect(() => {
    let active = true;
    setLoading(true);

    Promise.all([
      fetchFilterResults(requestId),
      fetchFilterCycles(requestId),
      fetchFilterMetals(requestId),
    ])
      .then(([r, c, m]) => {
        if (!active) return;
        setResults([...r].sort(byParamOrder));
        setCycles(c);
        setItems(m);
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

  const isTimeFilter = filterBy === 'time';

  // Unselected parameters were never computed, so they are simply absent from `results`. The
  // outputs that are not plc_filtered_parameters rows — the impeller panel and the two data
  // tables — have no row to be absent, so their toggles are checked here instead.
  // An empty selection means the request predates the toggles and computed everything.
  const selectedAll  = selectedParameters.length === 0;
  const isSelected   = (k: Section2ParamKey) => selectedAll || selectedParameters.includes(k);

  const showAmps       = isSelected('impeller_current');
  const showProduction = isSelected('production_qty_kg');

  // plc_filtered_cycle_data is written whenever anything cycle-derived was selected, so the cycle
  // table follows the same condition rather than a toggle of its own.
  const showCycleTable =
    isSelected('energy_kwh_total') || isSelected('energy_per_casting_kwh_kg') ||
    isSelected('blast_time_sec')   || isSelected('cycle_count')               ||
    isSelected('impeller_current');

  if (!results.length && !showAmps) {
    return <Alert severity="info">No results found for the selected filter.</Alert>;
  }

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
      case 'production_qty_kg':
        return () => <ItemProductionGraph requestId={requestId} />;
      case 'energy_kwh_total':
        return () => <CycleDataGraph requestId={requestId} />;
      // Blast time and cycle count are bucketed over the filter window, the same series and the
      // same component Section 1 uses — so the two sections show the same shape at two scopes
      // rather than one per-cycle chart and one per-day chart that cannot be compared.
      case 'blast_time_sec':
        return isTimeFilter
          ? () => <TrendMetricGraph metric="blastTime" windowStart={filterStart} windowEnd={filterEnd} />
          : undefined;
      case 'cycle_count':
        return isTimeFilter
          ? () => <TrendMetricGraph metric="cycleCount" windowStart={filterStart} windowEnd={filterEnd} />
          : undefined;
      default:
        return undefined;
    }
  }

  return (
    <Box>
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" flexWrap="wrap" gap={1} mb={2}>
        <Box>
          <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem', mb: 0.5 }}>
            Filtered Parameters
          </Typography>

          {/* The item being viewed is the easiest scope to lose track of, so it gets a chip of its
              own rather than only appearing inside the label text. */}
          {itemName && (
            <Chip
              label={`Casting item: ${itemName}`}
              color="primary"
              variant="outlined"
              size="small"
              sx={{ mb: 0.5, fontWeight: 600 }}
            />
          )}

          <Typography variant="caption" color="text.secondary" display="block">
            {isTimeFilter
              ? `${new Date(filterStart).toLocaleString()} to ${new Date(filterEnd).toLocaleString()}`
              : label}
          </Typography>

          {itemName && (
            <Typography variant="caption" color="text.secondary" display="block">
              Every value below is computed from only the cycles that declared this item.
              Machine Utility is not shown: machine on-time is not attributable to a single item.
            </Typography>
          )}
        </Box>

        <Button
          variant="outlined"
          size="small"
          startIcon={<DownloadIcon />}
          onClick={() => exportFilteredWorkbook({
            label, filterBy, filterStart, filterEnd, itemName, results, cycles, items,
          })}
        >
          Export
        </Button>
      </Box>

      {/* Scalar tiles — same canonical order as Section 1. Tap a tile to open its graph. */}
      {results.length > 0 && (
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
              section={2}
              graphTitle={graphTitle(r.parameterName)}
              renderGraph={renderGraph(r.parameterName)}
            />
          ))}
        </Box>
      )}

      {/* ── Data tables ──────────────────────────────────────────────────────────────────
          These two carry information the tiles do NOT: the per-item split behind the single
          Production total, and what each individual cycle contributed. The removed table was the
          "Parameters" one, which only restated the tiles above it. */}

      {showProduction && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <SectionHeading title="Production by Item" />
          {items.length > 0
            ? <ItemProductionTable items={items} />
            : <Alert severity="info">No casting item weights were declared for the cycles in this filter.</Alert>}
        </>
      )}

      {showCycleTable && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <SectionHeading
            title="Cycle Log"
            count={`${cycles.length} ${cycles.length === 1 ? 'cycle' : 'cycles'}`}
          />
          {cycles.length > 0
            ? <CycleTable cycles={cycles} />
            : <Alert severity="info">No completed blast cycles fall within this filter.</Alert>}
        </>
      )}

      {showAmps && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <SectionHeading title="Impeller Current (Filtered)" />
          <FilteredAmpsPanel requestId={requestId} />
        </>
      )}
    </Box>
  );
}
