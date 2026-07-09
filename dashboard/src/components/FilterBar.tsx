import { useState } from 'react';
import {
  Box, Paper, Typography, Button, ButtonGroup, TextField,
  Tabs, Tab, LinearProgress, Alert, Chip,
} from '@mui/material';
import FilterAltOffIcon from '@mui/icons-material/FilterAltOff';
import type { FilterRequest, PeriodLabel } from '../types';

const PERIOD_BUTTONS: { label: string; value: PeriodLabel }[] = [
  { label: 'Hour',  value: 'hour'  },
  { label: 'Shift', value: 'shift' },
  { label: 'Day',   value: 'day'   },
  { label: 'Week',  value: 'week'  },
  { label: 'Month', value: 'month' },
  { label: 'Year',  value: 'year'  },
];

function periodToRange(period: PeriodLabel): { start: string; end: string } {
  const now  = new Date();
  const from = new Date(now);
  switch (period) {
    case 'hour':  from.setHours(now.getHours() - 1);       break;
    case 'shift': from.setHours(now.getHours() - 8);       break;
    case 'day':   from.setDate(now.getDate() - 1);         break;
    case 'week':  from.setDate(now.getDate() - 7);         break;
    case 'month': from.setMonth(now.getMonth() - 1);       break;
    case 'year':  from.setFullYear(now.getFullYear() - 1); break;
  }
  return { start: from.toISOString().slice(0, 16), end: now.toISOString().slice(0, 16) };
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
  const [metalName, setMetalName]       = useState('');
  const [validationMsg, setValidationMsg] = useState<string | null>(null);

  const busy = calcState === 'submitting' || calcState === 'polling';

  function handleApply() {
    setValidationMsg(null);
    const now = new Date().toISOString();

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
        filterStart = new Date(range.start).toISOString();
        filterEnd   = new Date(range.end).toISOString();
        periodLabel = activePeriod;
      }

      onApply({ filterStart, filterEnd, periodLabel, filterBy: 'time' });

    } else if (filterMode === 'cycle') {
      if (!cycleFrom || !cycleTo) { setValidationMsg('Enter both cycle from and to numbers.'); return; }
      const cf = parseInt(cycleFrom, 10);
      const ct = parseInt(cycleTo, 10);
      if (cf > ct) { setValidationMsg('Cycle From must be ≤ Cycle To.'); return; }
      onApply({ filterStart: now, filterEnd: now, filterBy: 'cycle', filterCycleFrom: cf, filterCycleTo: ct });

    } else {
      if (!metalName.trim()) { setValidationMsg('Enter a metal name.'); return; }
      onApply({ filterStart: now, filterEnd: now, filterBy: 'metal', filterMetalName: metalName.trim() });
    }
  }

  // Active filter banner
  if (calcState === 'done' && appliedContext) {
    const { label, filterStart, filterEnd, filterBy } = appliedContext;
    const rangeStr = filterBy === 'time'
      ? ` · ${new Date(filterStart).toLocaleString()} → ${new Date(filterEnd).toLocaleString()}`
      : '';
    return (
      <Paper sx={{ px: 2.5, py: 1.5, mb: 3, bgcolor: 'primary.50', border: '1px solid', borderColor: 'primary.200' }}>
        <Box display="flex" alignItems="center" justifyContent="space-between" flexWrap="wrap" gap={1}>
          <Box display="flex" alignItems="center" gap={1}>
            <Chip label="Filtered" color="primary" size="small" />
            <Typography variant="body2" fontWeight={600}>{label}{rangeStr}</Typography>
          </Box>
          <Button
            size="small"
            variant="outlined"
            startIcon={<FilterAltOffIcon />}
            onClick={onClear}
          >
            Clear Filter
          </Button>
        </Box>
      </Paper>
    );
  }

  return (
    <Paper sx={{ p: 2, mb: 3 }}>
      <Typography variant="subtitle2" fontWeight={700} sx={{ mb: 1.5 }}>
        Filter Parameters
      </Typography>

      <Tabs
        value={filterMode}
        onChange={(_, v: 'time' | 'cycle' | 'metal') => { setFilterMode(v); setValidationMsg(null); }}
        sx={{ mb: 2, minHeight: 36 }}
        TabIndicatorProps={{ style: { height: 3 } }}
      >
        <Tab label="Time Range" value="time"  sx={{ minHeight: 36 }} />
        <Tab label="Cycle Range" value="cycle" sx={{ minHeight: 36 }} />
        <Tab label="Metal"       value="metal" sx={{ minHeight: 36 }} />
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
          <TextField label="Metal Name" size="small" placeholder="e.g. Aluminium"
            value={metalName} onChange={e => setMetalName(e.target.value)} sx={{ width: 240 }} />
        </Box>
      )}

      {validationMsg && (
        <Alert severity="warning" sx={{ mb: 1.5 }}>{validationMsg}</Alert>
      )}

      {(calcState === 'submitting' || calcState === 'polling') && (
        <Box mb={1.5}>
          <LinearProgress />
          <Typography variant="caption" color="text.secondary" display="block" mt={0.5}>
            {calcState === 'submitting' ? 'Submitting…' : 'Calculating — please wait…'}
          </Typography>
        </Box>
      )}

      {calcState === 'error' && (
        <Alert severity="error" sx={{ mb: 1.5 }}>Calculation failed. Please try again.</Alert>
      )}

      <Button
        variant="contained"
        onClick={handleApply}
        disabled={busy}
        size="small"
      >
        {busy ? 'Calculating…' : 'Apply Filter'}
      </Button>
    </Paper>
  );
}
