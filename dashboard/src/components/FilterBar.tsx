import { useState } from 'react';
import {
  Box, Paper, Typography, Button, ButtonGroup, TextField,
  Tabs, Tab, LinearProgress, Alert, Chip, FormControlLabel, Checkbox, Tooltip, Divider,
} from '@mui/material';
import FilterAltOffIcon from '@mui/icons-material/FilterAltOff';
import { PARAM_META } from '../utils/unitConverters';
import {
  SECTION2_PARAM_KEYS, ITEM_FILTER_UNSUPPORTED,
  type FilterRequest, type PeriodLabel, type Section2ParamKey,
} from '../types';

const PERIOD_BUTTONS: { label: string; value: PeriodLabel }[] = [
  { label: 'Hour',  value: 'hour'  },
  { label: 'Shift', value: 'shift' },
  { label: 'Yesterday', value: 'day' },
  { label: 'Week',  value: 'week'  },
  { label: 'Month', value: 'month' },
  { label: 'Year',  value: 'year'  },
];

// Toggle rows carry the tile name and nothing else — every row used to repeat its own formula
// underneath, which said no more than the name already did. These control SECTION 2, so a
// parameter with a section-specific name takes that one. impeller_current has no PARAM_META entry
// because it is a panel, not a plc_filtered_parameters row, so it carries its own label.
const section2Name = (key: keyof typeof PARAM_META): string =>
  PARAM_META[key].section2Label ?? PARAM_META[key].label;

const PARAM_TOGGLES: { key: Section2ParamKey; label: string }[] = [
  { key: 'machine_utility_pct',       label: section2Name('machine_utility_pct') },
  { key: 'production_qty_kg',         label: section2Name('production_qty_kg') },
  { key: 'energy_kwh_total',          label: section2Name('energy_kwh_total') },
  { key: 'energy_per_casting_kwh_kg', label: section2Name('energy_per_casting_kwh_kg') },
  { key: 'blast_time_sec',            label: section2Name('blast_time_sec') },
  { key: 'cycle_count',               label: section2Name('cycle_count') },
  { key: 'impeller_current',          label: 'Impeller Current' },
];

/**
 * The window a preset button covers.
 *
 * Returns Date objects, NOT `toISOString().slice(0, 16)` strings. That slice dropped the trailing
 * "Z", so `new Date(...)` re-parsed a UTC clock reading as LOCAL time and slid every preset window
 * back by the local UTC offset — 5h30m in IST. A "Day" applied at 07:30 fetched 02:00 to 02:00
 * instead of 07:30 to 07:30. The slice existed to match the datetime-local input format, but the
 * custom-range fields hold their own state and never read this, so nothing needed that shape.
 *
 * "Day" is the odd one out by design: it is the previous COMPLETE calendar day rather than a
 * rolling 24 hours, so it always covers one whole day and never spills onto a second date. The
 * rolling form put the tail of the window on tomorrow's date whenever it was applied after
 * midnight. Every other preset is still a rolling window ending now.
 */
function periodToRange(period: PeriodLabel): { start: Date; end: Date } {
  const now = new Date();

  if (period === 'day') {
    const start = new Date(now);
    start.setDate(now.getDate() - 1);
    start.setHours(0, 0, 0, 0);
    const end = new Date(start);
    end.setDate(start.getDate() + 1);   // Date rolls month/year boundaries over for us.
    return { start, end };
  }

  const from = new Date(now);
  switch (period) {
    case 'hour':  from.setHours(now.getHours() - 1);       break;
    case 'shift': from.setHours(now.getHours() - 8);       break;
    case 'week':  from.setDate(now.getDate() - 7);         break;
    case 'month': from.setMonth(now.getMonth() - 1);       break;
    case 'year':  from.setFullYear(now.getFullYear() - 1); break;
  }
  return { start: from, end: now };
}

export type FilterCalcState = 'idle' | 'submitting' | 'polling' | 'done' | 'error';

interface AppliedContext {
  label: string;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
}

interface Props {
  calcState: FilterCalcState;
  appliedContext: AppliedContext | null;
  onApply: (req: FilterRequest) => void;
  onClear: () => void;
}

export default function FilterBar({ calcState, appliedContext, onApply, onClear }: Props) {
  const [filterMode, setFilterMode] = useState<'time' | 'cycle' | 'metal'>('time');
  const [activePeriod, setActivePeriod] = useState<PeriodLabel | 'custom'>('day');
  const [customStart, setCustomStart]   = useState('');
  const [customEnd, setCustomEnd]       = useState('');
  const [cycleFrom, setCycleFrom]       = useState('');
  const [cycleTo, setCycleTo]           = useState('');
  const [itemName, setItemName]         = useState('');
  const [validationMsg, setValidationMsg] = useState<string | null>(null);

  // Per-parameter calculation toggles. Default: everything selected. Held here (not in a URL or
  // localStorage) so the selection survives every apply/clear for as long as the page is open,
  // and resets to "all" on reload — the safe default for a shared plant terminal.
  const [selected, setSelected] = useState<Set<Section2ParamKey>>(
    () => new Set(SECTION2_PARAM_KEYS),
  );

  const busy = calcState === 'submitting' || calcState === 'polling';
  const isFiltered = calcState === 'done' && appliedContext != null;

  // machine_utility_pct is machine-level: its denominator is the machine being powered up —
  // blasting anything, or idle — which no single casting item owns a share of. Rather than show a
  // number computed across other items' cycles, the toggle is disabled under an item filter.
  const isItemMode = filterMode === 'metal';
  const isDisabledParam = (key: Section2ParamKey) =>
    isItemMode && ITEM_FILTER_UNSUPPORTED.includes(key);

  // What would actually be sent — a disabled toggle never counts, even if it was ticked before
  // the user switched to Item mode.
  const effectiveSelected = SECTION2_PARAM_KEYS.filter(k => selected.has(k) && !isDisabledParam(k));
  const availableCount    = SECTION2_PARAM_KEYS.filter(k => !isDisabledParam(k)).length;
  const nothingSelected   = effectiveSelected.length === 0;

  function toggleParam(key: Section2ParamKey) {
    setSelected(prev => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key); else next.add(key);
      return next;
    });
  }

  const selectAll = () => setSelected(new Set(SECTION2_PARAM_KEYS));
  const clearAll  = () => setSelected(new Set());

  function handleApply() {
    setValidationMsg(null);
    const now = new Date().toISOString();

    // Guarded by the disabled Apply button too; this is the belt-and-braces half.
    if (nothingSelected) {
      setValidationMsg('Select at least one parameter to calculate.');
      return;
    }
    const selectedParameters = effectiveSelected;

    if (filterMode === 'time') {
      let filterStart: string;
      let filterEnd: string;
      let periodLabel: string | null;

      if (activePeriod === 'custom') {
        if (!customStart || !customEnd) { setValidationMsg('Enter both start and end dates.'); return; }
        if (customStart >= customEnd)   { setValidationMsg('Start must be before end.'); return; }
        filterStart = new Date(customStart).toISOString();
        filterEnd   = new Date(customEnd).toISOString();
        periodLabel = null;
      } else {
        const range = periodToRange(activePeriod);
        filterStart = range.start.toISOString();
        filterEnd   = range.end.toISOString();
        periodLabel = activePeriod;
      }

      onApply({ filterStart, filterEnd, periodLabel, filterBy: 'time', selectedParameters });

    } else if (filterMode === 'cycle') {
      if (!cycleFrom || !cycleTo) { setValidationMsg('Enter both cycle from and to numbers.'); return; }
      const cf = parseInt(cycleFrom, 10);
      const ct = parseInt(cycleTo, 10);
      if (cf > ct) { setValidationMsg('Cycle From must be ≤ Cycle To.'); return; }
      onApply({
        filterStart: now, filterEnd: now, filterBy: 'cycle',
        filterCycleFrom: cf, filterCycleTo: ct, selectedParameters,
      });

    } else {
      if (!itemName.trim()) { setValidationMsg('Enter an item name.'); return; }
      // The wire field is still filterMetalName — only the wording the user sees changed.
      onApply({
        filterStart: now, filterEnd: now, filterBy: 'metal',
        filterMetalName: itemName.trim(), selectedParameters,
      });
    }
  }

  return (
    <Paper sx={{ p: { xs: 2, md: 2.5 }, mb: 3 }}>
      {/* ── Header ─────────────────────────────────────────────────────────────────────── */}
      <Box display="flex" alignItems="flex-start" justifyContent="space-between" flexWrap="wrap" gap={1} mb={2}>
        <Box>
          <Typography variant="subtitle1" fontWeight={700} sx={{ fontSize: '1rem' }}>
            Filters
          </Typography>
        </Box>

        <Box display="flex" alignItems="center" gap={1} flexWrap="wrap">
          {isFiltered && (
            <>
              <Chip label={appliedContext!.label} color="primary" size="small" />
              <Button size="small" variant="outlined" startIcon={<FilterAltOffIcon />} onClick={onClear} disabled={busy}>
                Reset
              </Button>
            </>
          )}
        </Box>
      </Box>

      {/* ── Body: criteria on the left, the vertical parameter list on the right ────────── */}
      <Box
        sx={{
          display: 'grid',
          gridTemplateColumns: { xs: '1fr', md: 'minmax(0, 1fr) 300px' },
          gap: { xs: 2, md: 3 },
          alignItems: 'start',
        }}
      >
        {/* ── Left: what to filter by ── */}
        <Box>
          <Tabs
            value={filterMode}
            onChange={(_, v: 'time' | 'cycle' | 'metal') => { setFilterMode(v); setValidationMsg(null); }}
            sx={{ mb: 2, minHeight: 36, borderBottom: '1px solid', borderColor: 'divider' }}
            TabIndicatorProps={{ style: { height: 3 } }}
          >
            <Tab label="Time Range"  value="time"  sx={{ minHeight: 36 }} />
            <Tab label="Cycle Range" value="cycle" sx={{ minHeight: 36 }} />
            <Tab label="Item"        value="metal" sx={{ minHeight: 36 }} />
          </Tabs>

          {filterMode === 'time' && (
            <>
              <Box display="flex" flexWrap="wrap" gap={1} mb={2}>
                <ButtonGroup size="small" variant="outlined">
                  {PERIOD_BUTTONS.map(p => (
                    <Button
                      key={p.value}
                      variant={activePeriod === p.value ? 'contained' : 'outlined'}
                      onClick={() => setActivePeriod(p.value)}
                    >
                      {p.label}
                    </Button>
                  ))}
                  <Button
                    variant={activePeriod === 'custom' ? 'contained' : 'outlined'}
                    onClick={() => setActivePeriod('custom')}
                  >
                    Custom
                  </Button>
                </ButtonGroup>
              </Box>
              {activePeriod === 'custom' && (
                <Box display="flex" gap={2} mb={2} flexWrap="wrap">
                  <TextField label="Start" type="datetime-local" size="small"
                    slotProps={{ inputLabel: { shrink: true } }}
                    value={customStart} onChange={e => setCustomStart(e.target.value)} sx={{ minWidth: 220 }} />
                  <TextField label="End" type="datetime-local" size="small"
                    slotProps={{ inputLabel: { shrink: true } }}
                    value={customEnd} onChange={e => setCustomEnd(e.target.value)} sx={{ minWidth: 220 }} />
                </Box>
              )}
            </>
          )}

          {filterMode === 'cycle' && (
            <Box display="flex" gap={2} mb={2} flexWrap="wrap">
              <TextField label="Cycle From" type="number" size="small"
                value={cycleFrom} onChange={e => setCycleFrom(e.target.value)} sx={{ width: 140 }} />
              <TextField label="Cycle To" type="number" size="small"
                value={cycleTo} onChange={e => setCycleTo(e.target.value)} sx={{ width: 140 }} />
            </Box>
          )}

          {filterMode === 'metal' && (
            <Box mb={2}>
              <TextField label="Item Name" size="small" placeholder="e.g. Aluminium"
                value={itemName} onChange={e => setItemName(e.target.value)} sx={{ width: 260 }} />
              <Typography variant="caption" color="text.secondary" display="block" sx={{ mt: 0.75 }}>
                Matches the declared casting item name exactly. Every selected parameter is then
                computed from only the cycles that declared it.
              </Typography>
            </Box>
          )}

          {validationMsg && <Alert severity="warning" sx={{ mb: 1.5 }}>{validationMsg}</Alert>}

          {busy && (
            <Box mb={1.5}>
              <LinearProgress />
              <Typography variant="caption" color="text.secondary" display="block" mt={0.5}>
                {calcState === 'submitting' ? 'Submitting…' : 'Calculating, please wait…'}
              </Typography>
            </Box>
          )}

          {calcState === 'error' && (
            <Alert severity="error" sx={{ mb: 1.5 }}>Calculation failed. Please try again.</Alert>
          )}

          <Button
            variant="contained"
            onClick={handleApply}
            disabled={busy || nothingSelected}
            size="medium"
            sx={{ minWidth: 150 }}
          >
            {busy ? 'Calculating…' : 'Apply Filter'}
          </Button>
        </Box>

        {/* ── Right: which parameters to calculate ──
            One row per parameter, stacked vertically. Only ticked parameters are computed at
            all — the backend skips their queries rather than computing everything and trimming
            the response. */}
        <Paper
          variant="outlined"
          sx={{ borderRadius: 2, overflow: 'hidden', bgcolor: 'background.paper' }}
        >
          <Box
            sx={{
              px: 1.75, py: 1.25,
              bgcolor: 'action.hover',
              borderBottom: '1px solid',
              borderColor: 'divider',
              display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 1,
            }}
          >
            <Typography variant="subtitle2" fontWeight={700}>
              Parameters
            </Typography>
            <Chip
              size="small"
              label={`${effectiveSelected.length}/${availableCount}`}
              color={nothingSelected ? 'default' : 'primary'}
              variant={nothingSelected ? 'outlined' : 'filled'}
              sx={{ fontWeight: 600, height: 20, fontSize: '0.7rem' }}
            />
          </Box>

          <Box sx={{ py: 0.5 }}>
            {PARAM_TOGGLES.map(({ key, label }) => {
              const disabled = isDisabledParam(key);
              const row = (
                <FormControlLabel
                  disabled={busy || disabled}
                  sx={{
                    display: 'flex',
                    alignItems: 'center',
                    width: '100%',
                    m: 0,
                    px: 1.25, py: 0.6,
                    transition: 'background-color 0.12s',
                    '&:hover': { bgcolor: disabled ? 'transparent' : 'action.hover' },
                  }}
                  control={
                    <Checkbox
                      size="small"
                      sx={{ py: 0.25, mr: 0.25 }}
                      checked={selected.has(key) && !disabled}
                      onChange={() => toggleParam(key)}
                    />
                  }
                  label={
                    <Typography variant="body2" sx={{ fontWeight: 500, lineHeight: 1.35, minWidth: 0 }}>
                      {label}
                    </Typography>
                  }
                />
              );

              return disabled ? (
                <Tooltip
                  key={key}
                  title="Machine on-time is not attributable to a single casting item, so this cannot be scoped by item"
                  placement="left"
                >
                  {/* A disabled control swallows pointer events, so the tooltip needs a live wrapper. */}
                  <Box>{row}</Box>
                </Tooltip>
              ) : (
                <Box key={key}>{row}</Box>
              );
            })}
          </Box>

          <Divider />

          <Box sx={{ px: 1, py: 0.5, display: 'flex', gap: 0.5 }}>
            <Button size="small" onClick={selectAll} disabled={busy} sx={{ flex: 1, fontSize: '0.75rem' }}>
              Select All
            </Button>
            <Button size="small" onClick={clearAll} disabled={busy} sx={{ flex: 1, fontSize: '0.75rem' }}>
              Clear All
            </Button>
          </Box>
        </Paper>
      </Box>
    </Paper>
  );
}
