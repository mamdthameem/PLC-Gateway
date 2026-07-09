import React, { useState, useRef } from 'react';
import {
  Box, Typography, Button, ButtonGroup, TextField, Alert,
  Divider, LinearProgress, Tab, Tabs,
  Table, TableBody, TableCell, TableContainer, TableHead, TableRow, Paper,
} from '@mui/material';
import {
  submitFilterRequest, pollFilterStatus, fetchFilterResults,
  fetchFilterCycles, fetchFilterShots,
} from '../services/filterService';
import { ParameterCard } from './ParameterCard';
import ShotsBreakdownChart from './ShotsBreakdownChart';
import type { FilterResult, FilteredCycle, ShotsBreakdownEntry, PeriodLabel } from '../types';

const PERIOD_BUTTONS: { label: string; value: PeriodLabel }[] = [
  { label: 'Hour',  value: 'hour'  },
  { label: 'Shift', value: 'shift' },
  { label: 'Day',   value: 'day'   },
  { label: 'Week',  value: 'week'  },
  { label: 'Month', value: 'month' },
  { label: 'Year',  value: 'year'  },
];

function periodToRange(period: PeriodLabel): { start: string; end: string } {
  const now = new Date();
  const end = now.toISOString().slice(0, 16);
  const from = new Date(now);
  switch (period) {
    case 'hour':  from.setHours(now.getHours() - 1);       break;
    case 'shift': from.setHours(now.getHours() - 8);       break;
    case 'day':   from.setDate(now.getDate() - 1);         break;
    case 'week':  from.setDate(now.getDate() - 7);         break;
    case 'month': from.setMonth(now.getMonth() - 1);       break;
    case 'year':  from.setFullYear(now.getFullYear() - 1); break;
  }
  return { start: from.toISOString().slice(0, 16), end };
}

type CalcState = 'idle' | 'submitting' | 'polling' | 'done' | 'error';
type FilterMode = 'time' | 'cycle' | 'metal';

function CycleTable({ cycles }: { cycles: FilteredCycle[] }) {
  if (cycles.length === 0) return null;
  return (
    <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto', mt: 2 }}>
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
            <TableCell align="right">Shots</TableCell>
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
              <TableCell align="right">{c.energyKwh.toFixed(2)}</TableCell>
              <TableCell align="right">{c.shotsUsage.toFixed(1)}</TableCell>
            </TableRow>
          ))}
        </TableBody>
      </Table>
    </TableContainer>
  );
}

export const FilterSection: React.FC = () => {
  const [filterMode, setFilterMode] = useState<FilterMode>('time');
  const [activePeriod, setActivePeriod] = useState<PeriodLabel | 'custom'>('day');
  const [customStart, setCustomStart] = useState('');
  const [customEnd, setCustomEnd]     = useState('');
  const [cycleFrom, setCycleFrom]     = useState('');
  const [cycleTo, setCycleTo]         = useState('');
  const [metalName, setMetalName]     = useState('');

  const [calcState, setCalcState] = useState<CalcState>('idle');
  const [message, setMessage]     = useState<string | null>(null);
  const [results, setResults]     = useState<FilterResult[]>([]);
  const [cycles, setCycles]       = useState<FilteredCycle[]>([]);
  const [shots, setShots]         = useState<ShotsBreakdownEntry[]>([]);
  const [resultTab, setResultTab] = useState(0);
  const [filterContext, setFilterContext] = useState<{ label: string; start: string; end: string } | null>(null);

  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const stopPolling = () => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
  };

  const handleSubmit = async () => {
    setMessage(null);
    setResults([]);
    setCycles([]);
    setShots([]);
    setResultTab(0);

    let filterStart = '';
    let filterEnd = '';
    let periodLabel: string | null = null;
    let filterCycleFrom: number | null = null;
    let filterCycleTo: number | null = null;
    let filterMetalName: string | null = null;

    if (filterMode === 'time') {
      if (activePeriod === 'custom') {
        if (!customStart || !customEnd) { setMessage('Please fill in both start and end dates.'); return; }
        if (customStart >= customEnd) { setMessage('Start date must be before end date.'); return; }
        filterStart = new Date(customStart).toISOString();
        filterEnd   = new Date(customEnd).toISOString();
      } else {
        const range = periodToRange(activePeriod);
        filterStart = new Date(range.start).toISOString();
        filterEnd   = new Date(range.end).toISOString();
        periodLabel = activePeriod;
      }
      setFilterContext({
        label: activePeriod === 'custom' ? 'Custom range' : activePeriod.charAt(0).toUpperCase() + activePeriod.slice(1),
        start: filterStart,
        end: filterEnd,
      });
    } else if (filterMode === 'cycle') {
      if (!cycleFrom || !cycleTo) { setMessage('Please enter both cycle from and to numbers.'); return; }
      filterCycleFrom = parseInt(cycleFrom, 10);
      filterCycleTo   = parseInt(cycleTo, 10);
      if (filterCycleFrom > filterCycleTo) { setMessage('Cycle From must be ≤ Cycle To.'); return; }
      filterStart = new Date(0).toISOString();
      filterEnd   = new Date().toISOString();
      setFilterContext({ label: `Cycles ${cycleFrom}–${cycleTo}`, start: filterStart, end: filterEnd });
    } else {
      if (!metalName.trim()) { setMessage('Please enter a metal name.'); return; }
      filterMetalName = metalName.trim();
      filterStart = new Date(0).toISOString();
      filterEnd   = new Date().toISOString();
      setFilterContext({ label: `Metal: ${filterMetalName}`, start: filterStart, end: filterEnd });
    }

    setCalcState('submitting');
    stopPolling();

    try {
      const requestId = await submitFilterRequest({
        filterStart,
        filterEnd,
        periodLabel,
        filterBy: filterMode,
        filterCycleFrom,
        filterCycleTo,
        filterMetalName,
      });
      setCalcState('polling');

      pollRef.current = setInterval(async () => {
        try {
          const status = await pollFilterStatus(requestId);
          if (status.status === 'done') {
            stopPolling();
            const [paramData, cycleData, shotData] = await Promise.all([
              fetchFilterResults(requestId),
              fetchFilterCycles(requestId),
              fetchFilterShots(requestId),
            ]);
            setResults(paramData);
            setCycles(cycleData);
            setShots(shotData);
            setCalcState('done');
          } else if (status.status === 'error') {
            stopPolling();
            setCalcState('error');
            setMessage('Backend reported an error processing this request. Please try again.');
          }
        } catch (err) {
          stopPolling();
          setCalcState('error');
          setMessage(err instanceof Error ? err.message : 'Polling failed');
        }
      }, 2500);
    } catch (err) {
      setCalcState('error');
      setMessage(err instanceof Error ? err.message : 'Failed to submit filter request');
    }
  };

  return (
    <Box>
      <Box mb={2}>
        <Typography variant="h6" fontWeight={700} sx={{ fontSize: '1rem' }}>
          Filtered Parameters
        </Typography>
        <Typography variant="caption" color="text.secondary">
          Select a filter type and range to calculate parameters for that window
        </Typography>
      </Box>

      {/* Filter type tabs */}
      <Tabs
        value={filterMode}
        onChange={(_, v: FilterMode) => { setFilterMode(v); setMessage(null); }}
        sx={{ mb: 2 }}
        variant="scrollable"
        scrollButtons="auto"
      >
        <Tab label="Time Range" value="time" />
        <Tab label="Cycle Range" value="cycle" />
        <Tab label="Metal" value="metal" />
      </Tabs>

      {filterMode === 'time' && (
        <>
          <Box display="flex" flexWrap="wrap" gap={1} mb={2} alignItems="center">
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
              <TextField
                label="Start"
                type="datetime-local"
                size="small"
                slotProps={{ inputLabel: { shrink: true } }}
                value={customStart}
                onChange={e => setCustomStart(e.target.value)}
                sx={{ minWidth: 220 }}
              />
              <TextField
                label="End"
                type="datetime-local"
                size="small"
                slotProps={{ inputLabel: { shrink: true } }}
                value={customEnd}
                onChange={e => setCustomEnd(e.target.value)}
                sx={{ minWidth: 220 }}
              />
            </Box>
          )}
        </>
      )}

      {filterMode === 'cycle' && (
        <Box display="flex" gap={2} mb={2} flexWrap="wrap">
          <TextField
            label="Cycle From"
            type="number"
            size="small"
            value={cycleFrom}
            onChange={e => setCycleFrom(e.target.value)}
            sx={{ width: 140 }}
          />
          <TextField
            label="Cycle To"
            type="number"
            size="small"
            value={cycleTo}
            onChange={e => setCycleTo(e.target.value)}
            sx={{ width: 140 }}
          />
        </Box>
      )}

      {filterMode === 'metal' && (
        <Box mb={2}>
          <TextField
            label="Metal Name"
            size="small"
            value={metalName}
            onChange={e => setMetalName(e.target.value)}
            placeholder="e.g. Steel, Copper"
            sx={{ width: 240 }}
          />
        </Box>
      )}

      <Button
        variant="contained"
        onClick={handleSubmit}
        disabled={calcState === 'submitting' || calcState === 'polling'}
        sx={{ mb: 2 }}
      >
        {calcState === 'submitting' || calcState === 'polling' ? 'Calculating…' : 'Calculate'}
      </Button>

      {(calcState === 'submitting' || calcState === 'polling') && (
        <Box mb={2}>
          <LinearProgress />
          <Typography variant="caption" color="text.secondary" mt={0.5} display="block">
            {calcState === 'submitting' ? 'Submitting request…' : 'Waiting for backend calculation…'}
          </Typography>
        </Box>
      )}

      {message && (
        <Alert severity={calcState === 'error' ? 'error' : 'warning'} sx={{ mb: 2 }}>
          {message}
        </Alert>
      )}

      {calcState === 'done' && results.length === 0 && (
        <Alert severity="info">No data found for the selected filter.</Alert>
      )}

      {calcState === 'done' && results.length > 0 && (
        <>
          <Divider sx={{ mb: 2 }} />
          <Box mb={1.5}>
            <Typography variant="body2" fontWeight={600}>{filterContext?.label}</Typography>
            {filterContext && filterMode === 'time' && (
              <Typography variant="caption" color="text.secondary">
                {new Date(filterContext.start).toLocaleString()} → {new Date(filterContext.end).toLocaleString()}
              </Typography>
            )}
          </Box>

          <Tabs value={resultTab} onChange={(_, v: number) => setResultTab(v)} sx={{ mb: 2 }}>
            <Tab label="Summary" />
            {cycles.length > 0 && <Tab label={`Cycles (${cycles.length})`} />}
            {shots.length > 0 && <Tab label="Shots Chart" />}
          </Tabs>

          {resultTab === 0 && (
            <Box
              sx={{
                display: 'grid',
                gridTemplateColumns: {
                  xs: '1fr',
                  sm: 'repeat(2, 1fr)',
                  md: 'repeat(3, 1fr)',
                  lg: 'repeat(4, 1fr)',
                },
                gap: 2,
              }}
            >
              {results.map(r => (
                <ParameterCard key={r.parameterName} parameterName={r.parameterName} value={r.value} />
              ))}
            </Box>
          )}

          {resultTab === 1 && cycles.length > 0 && <CycleTable cycles={cycles} />}

          {resultTab === (cycles.length > 0 ? 2 : 1) && shots.length > 0 && (
            <>
              <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 1 }}>
                Shot Refill Breakdown — Blasts per Refill
              </Typography>
              <ShotsBreakdownChart data={shots} />
            </>
          )}
        </>
      )}
    </Box>
  );
};
