import React, { useState, useRef, useCallback, useEffect } from 'react';
import { Box, Container, Divider } from '@mui/material';
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

// The all-time block above the filter shows everything Section 1 computes, in PARAM_ORDER —
// the unfilterable parameters and the ones that also have a Section 2 form.
const ALL_SECTION1_PARAM_KEYS: readonly string[] = [
  ...SECTION1_ONLY_PARAM_KEYS,
  ...SHARED_PARAM_KEYS,
];

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

/** Display names for presets whose button label is not just the capitalised wire value. */
const PERIOD_DISPLAY: Record<string, string> = { day: 'Yesterday' };

function filterLabel(req: FilterRequest): string {
  if (req.filterBy === 'cycle') return `Cycles ${req.filterCycleFrom}–${req.filterCycleTo}`;
  // The wire field is filterMetalName; the user-facing word is "Item".
  if (req.filterBy === 'metal') return `Item: ${req.filterMetalName}`;
  if (req.periodLabel) {
    return PERIOD_DISPLAY[req.periodLabel]
      ?? req.periodLabel.charAt(0).toUpperCase() + req.periodLabel.slice(1);
  }
  return 'Custom range';
}

/**
 * The dashboard is three blocks:
 *
 *   1. ABOVE THE FILTER — the COMPLETE Section 1 picture. Machine status, the Section 1-only
 *      parameters (the two refill figures, effective shots usage), the parameters that also exist
 *      in Section 2, the shots-per-refill chart, live impeller current and spare health. Read top
 *      to bottom, this block answers "how is the machine doing, all time?" without the reader
 *      having to apply a filter or scroll past one.
 *
 *   2. THE FILTER BAR.
 *
 *   3. BELOW THE FILTER — the parameters that exist in BOTH sections. Unfiltered they show their
 *      Section 1 values; applying a filter replaces them with the Section 2 values for the chosen
 *      scope.
 *
 * The shared parameters therefore appear TWICE while no filter is applied — once in the all-time
 * block above, once below. That repetition is deliberate and was asked for: the upper block is a
 * fixed all-time reference that never moves, and the lower block is the one that changes when a
 * filter is applied, so a reader can compare a filtered figure against its all-time counterpart
 * without clearing the filter. An earlier revision removed the duplication; it is back by request.
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
        include={ALL_SECTION1_PARAM_KEYS}
        title="Lifetime Parameters"
        showShotsChart
      />

      <Divider sx={{ mt: 3, mb: 2 }} />

      {/* Live impeller current belongs to the all-time block too: unfiltered it is the 1 s
          reading, which is a Section 1 fact. AmpsPanel renders its own heading. */}
      <AmpsPanel />

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
            title="Filtered Parameters"
            subtitle="No filter applied"
          />

        </Box>
      )}
    </Container>
  );
};
