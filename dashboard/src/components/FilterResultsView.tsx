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
            <TableCell sx={{ fontWeight: 700 }}>Casting Item</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Declared Weight (kg)</TableCell>
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
            <TableCell sx={{ fontWeight: 700 }}>Cycle #</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Start</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>End</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 1</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 2</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 3</TableCell>
            <TableCell sx={{ fontWeight: 700 }}>Item 4</TableCell>
            <TableCell align="right" sx={{ fontWeight: 700 }}>Production (kg)</TableCell>
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
// Utility is the exception: its graph is a time series, and cycle/item filters carry no
// meaningful time axis (filter_start/filter_end are NOT NULL, so both are set to NOW() as a
// placeholder). Its tile therefore has no graph in those modes — the scalar still shows.
const GRAPHABLE_ALL_MODES: Record<string, string> = {
  production_qty_kg:         'Production per Casting Item',
  energy_kwh_total:          'Energy per Cycle',
  energy_per_casting_kwh_kg: 'Efficiency per Cycle (kWh/kg)',
};
const GRAPHABLE_TIME_ONLY: Record<string, string> = {
  machine_utility_pct: 'Utility Trend',
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
        return () => <CycleDataGraph requestId={requestId} mode="energy" />;
      case 'energy_per_casting_kwh_kg':
        return () => <CycleDataGraph requestId={requestId} mode="efficiency" />;
      // blast_time_sec and cycle_count are scalar-only in BOTH sections. Section 1 has no
      // plc_daily_trends column to plot them from; Section 2 could have plotted them per cycle but
      // the asymmetry between the sections was more confusing than the charts were useful. Their
      // per-cycle detail is still on screen — in the Cycle Breakdown table below.
      default:
        return undefined;
    }
  }

  return (
    <Box>
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" flexWrap="wrap" gap={1} mb={2}>
        <Box>
          <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem', mb: 0.5 }}>
            Section 2 — Filtered · {label}
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

          {isTimeFilter && (
            <Typography variant="caption" color="text.secondary" display="block">
              {new Date(filterStart).toLocaleString()} → {new Date(filterEnd).toLocaleString()}
            </Typography>
          )}

          {itemName && (
            <Typography variant="caption" color="text.secondary" display="block">
              Every value below is computed from only the cycles that declared this item.
              Machine utility is not shown — machine on-time is not attributable to a single item.
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
          Download Excel
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
          <SectionHeading
            title="Production by Casting Item"
            note={itemName
              ? `Declared weight for "${itemName}" across the cycles that declared it. Other items declared in the same cycles are excluded from this scope.`
              : "Summed declared casting-item weights for this filter. Section 1 reports production from the PLC's Tonnage accumulator instead, so the two figures answer different questions and need not match."}
          />
          {items.length > 0
            ? <ItemProductionTable items={items} />
            : <Alert severity="info">No casting item weights were declared for the cycles in this filter.</Alert>}
        </>
      )}

      {showCycleTable && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <SectionHeading title={`Cycle Breakdown (${cycles.length} ${cycles.length === 1 ? 'cycle' : 'cycles'})`} />
          {cycles.length > 0
            ? <CycleTable cycles={cycles} />
            : <Alert severity="info">No completed blast cycles fall within this filter.</Alert>}
        </>
      )}

      {showAmps && (
        <>
          <Divider sx={{ mt: 3, mb: 2 }} />
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 0.25 }}>
            Impeller Current
          </Typography>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            Average current per impeller for this filter&apos;s cycles — same tile-and-chart layout
            as the live Amps panel, but scoped to the filter instead of live readings.
          </Typography>
          <FilteredAmpsPanel requestId={requestId} />
        </>
      )}
    </Box>
  );
}
