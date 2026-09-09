import React, { useState, useRef, useCallback, useEffect } from 'react';
import { Box, Container, Divider, Typography } from '@mui/material';
import FilterBar, { type FilterCalcState } from './FilterBar';
import MachineStatusTile from './MachineStatusTile';
import { LifetimeSection } from './LifetimeSection';
import AmpsPanel from './AmpsPanel';
import SpareHealthTable from './SpareHealthTable';
import FilterResultsView from './FilterResultsView';
import { submitFilterRequest, pollFilterStatus } from '../services/filterService';
import {
  SECTION1_ONLY_PARAM_KEYS, SHARED_PARAM_KEYS,
  type FilterRequest, type Section2ParamKey,
} from '../types';

interface AppliedContext {
  requestId: number;
  label: string;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
  /** Present only for an item filter — Section 2 shows it so the scope on screen is obvious. */
  itemName: string | null;
  /** The parameters this request actually computed; anything else was never calculated. */
  selectedParameters: Section2ParamKey[];
}

function filterLabel(req: FilterRequest): string {
  if (req.filterBy === 'cycle') return `Cycles ${req.filterCycleFrom}–${req.filterCycleTo}`;
  // The wire field is filterMetalName; the user-facing word is "Item".
  if (req.filterBy === 'metal') return `Item: ${req.filterMetalName}`;
  if (req.periodLabel) return req.periodLabel.charAt(0).toUpperCase() + req.periodLabel.slice(1) + ' view';
  return 'Custom range';
}

/**
 * The dashboard is three blocks, and which parameter sits in which block is the whole design:
 *
 *   1. ABOVE THE FILTER — parameters that exist ONLY in Section 1. Machine status, the two refill
 *      figures, effective shots usage, the shots-per-refill chart and spare health. None of these
 *      can be scoped by a filter, so the filter never touches them and they are never repeated
 *      below.
 *
 *   2. THE FILTER BAR.
 *
 *   3. BELOW THE FILTER — the parameters that exist in BOTH sections. With no filter applied they
 *      show their Section 1 (all-time, live) values; applying a filter replaces the same tiles
 *      with the Section 2 values for the chosen scope. Same parameters, same place on the page,
 *      different scope.
 *
 * Nothing is calculated until Apply is pressed. Section 2 is an on-demand computation that writes
 * a calculation_requests row, so firing one on page load would charge every visitor for a
 * calculation nobody asked for.
 */
export const MachineDashboard: React.FC = () => {
  const [calcState, setCalcState] = useState<FilterCalcState>('idle');
  const [applied, setApplied]     = useState<AppliedContext | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const stopPoll = () => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
  };

  // Stop polling if the page is left mid-calculation.
  useEffect(() => stopPoll, []);

  const handleApply = useCallback(async (req: FilterRequest) => {
    stopPoll();
    setCalcState('submitting');
    try {
      const requestId = await submitFilterRequest(req);
      setCalcState('polling');

      pollRef.current = setInterval(async () => {
        try {
          const status = await pollFilterStatus(requestId);
          if (status.status === 'done') {
            stopPoll();
            setApplied({
              requestId,
              label:       filterLabel(req),
              filterStart: req.filterStart,
              filterEnd:   req.filterEnd,
              filterBy:    req.filterBy,
              itemName:    req.filterBy === 'metal' ? (req.filterMetalName ?? null) : null,
              selectedParameters: req.selectedParameters ?? [],
            });
            setCalcState('done');
          } else if (status.status === 'error') {
            stopPoll();
            setCalcState('error');
          }
        } catch {
          stopPoll();
          setCalcState('error');
        }
      }, 2500);
    } catch {
      setCalcState('error');
    }
  }, []);

  // Clearing drops back to the Section 1 view of the same tiles — no calculation involved.
  const handleClear = useCallback(() => {
    stopPoll();
    setCalcState('idle');
    setApplied(null);
  }, []);

  const showSection2 = calcState === 'done' && applied !== null;

  return (
    <Container maxWidth="xl" sx={{ py: 3 }}>
      {/* ══════════ SECTION 1 ONLY — never filtered, never repeated below ══════════ */}

      {/* Machine status — 5 s poll, also carries the PLC link state */}
      <MachineStatusTile />

      <Divider sx={{ my: 3 }} />

      <LifetimeSection
        include={SECTION1_ONLY_PARAM_KEYS}
        title="Section 1 — Lifetime Parameters"
        subtitle="Cumulative since commissioning · these have no filtered equivalent and never respond to the filter"
        showShotsChart
      />

      <Divider sx={{ my: 3 }} />

      {/* Spare health grid — 10 s poll. Run-hours are per-spare lifetime counters from the PLC,
          so there is nothing for a filter to scope. */}
      <SpareHealthTable />

      <Divider sx={{ my: 4 }} />

      {/* ══════════ FILTER BAR ══════════ */}
      <FilterBar
        calcState={calcState}
        appliedContext={applied}
        onApply={handleApply}
        onClear={handleClear}
      />

      {/* ══════════ SHARED PARAMETERS — Section 1 live, or Section 2 once filtered ══════════ */}
      {showSection2 ? (
        <FilterResultsView
          requestId={applied!.requestId}
          filterStart={applied!.filterStart}
          filterEnd={applied!.filterEnd}
          filterBy={applied!.filterBy}
          itemName={applied!.itemName}
          label={applied!.label}
          selectedParameters={applied!.selectedParameters}
        />
      ) : (
        <Box>
          <LifetimeSection
            include={SHARED_PARAM_KEYS}
            title="Section 1 — Real-time"
            subtitle="All-time values, updating live · apply a filter above to see these same parameters for a chosen scope"
          />

          <Divider sx={{ mt: 3, mb: 2 }} />

          {/* Impeller current is the one shared output that is a panel rather than a tile.
              Unfiltered it is the live 1 s reading; filtered it becomes the per-cycle average. */}
          <Typography variant="subtitle2" fontWeight={600} sx={{ mb: 0.25 }}>
            Impeller Current
          </Typography>
          <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
            Live current per impeller, refreshed every second. Tap a tile for the last completed
            cycle&apos;s trace.
          </Typography>
          <AmpsPanel />
        </Box>
      )}
    </Container>
  );
};
