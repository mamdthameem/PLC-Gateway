import React, { useState, useRef, useCallback, useEffect } from 'react';
import { useSearchParams } from 'react-router-dom';
import { Alert, Box, CircularProgress, Container, Divider } from '@mui/material';
import FilterBar, { type FilterCalcState } from './FilterBar';
import MachineStatusTile from './MachineStatusTile';
import { LifetimeSection } from './LifetimeSection';
import AmpsPanel from './AmpsPanel';
import SpareHealthTable from './SpareHealthTable';
import FilterResultsView from './FilterResultsView';
import {
  submitFilterRequest, pollFilterStatus, fetchFilterRequest, FilterRequestError,
} from '../services/filterService';
import { filterToSearchParams, filterFromSearchParams, requestFromInfo } from '../utils/filterUrl';
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
function appliedFrom(requestId: number, req: FilterRequest): AppliedContext {
  return {
    requestId,
    label:       filterLabel(req),
    filterStart: req.filterStart,
    filterEnd:   req.filterEnd,
    filterBy:    req.filterBy,
    itemName:    req.filterBy === 'metal' ? (req.filterMetalName ?? null) : null,
    selectedParameters: req.selectedParameters ?? [],
  };
}

export const MachineDashboard: React.FC = () => {
  // The applied filter lives in THIS tab only: React state plus this tab's URL. Nothing about it is
  // kept on the server except the saved calculation itself, which is looked up by its own id - so
  // two people filtering at the same time cannot change each other's dashboard.
  const [searchParams, setSearchParams] = useSearchParams();
  const [calcState, setCalcState] = useState<FilterCalcState>('idle');
  const [applied, setApplied]     = useState<AppliedContext | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  // What the filter bar starts from: the filter in the URL (refined once the saved request is
  // loaded), else the defaults. A new key re-creates the bar so it picks up the reopened filter.
  const [barInitial, setBarInitial] = useState<FilterRequest | null>(() => filterFromSearchParams(searchParams).request);
  const [barKey, setBarKey]         = useState(0);
  const [restoring, setRestoring]   = useState(() => filterFromSearchParams(searchParams).resultId !== null);
  const [restoreNote, setRestoreNote] = useState<string | null>(null);

  const stopPoll = () => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
  };

  // Stop polling if the page is left mid-calculation.
  useEffect(() => stopPoll, []);

  const startPolling = useCallback((requestId: number, req: FilterRequest) => {
    stopPoll();
    setCalcState('polling');
    pollRef.current = setInterval(async () => {
      try {
        const status = await pollFilterStatus(requestId);
        if (status.status === 'done') {
          stopPoll();
          setApplied(appliedFrom(requestId, req));
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
  }, []);

  const handleApply = useCallback(async (req: FilterRequest) => {
    stopPoll();
    setRestoreNote(null);
    setCalcState('submitting');
    try {
      const requestId = await submitFilterRequest(req);
      // The URL names the result from the moment it exists, so a refresh - even mid-calculation -
      // picks the same calculation up again instead of starting a new one. Replace, not push: the
      // Back button keeps meaning "leave the dashboard", as it did before.
      setSearchParams(filterToSearchParams(req, requestId), { replace: true });
      startPolling(requestId, req);
    } catch {
      setCalcState('error');
    }
  }, [setSearchParams, startPolling]);

  // Clearing drops back to the Section 1 view of the same tiles — no calculation involved.
  const handleClear = useCallback(() => {
    stopPoll();
    setCalcState('idle');
    setApplied(null);
    setRestoreNote(null);
    setSearchParams({}, { replace: true });
  }, [setSearchParams]);

  // Reopen the result named in the URL, once, when the page opens. It is LOADED by id, never
  // recomputed: nothing is calculated until Apply is pressed. The lookup needs a signed-in user;
  // an expired sign-in signs out, and the login page brings the user back to this same link.
  useEffect(() => {
    const { request: fromUrl, resultId } = filterFromSearchParams(searchParams);
    if (resultId === null) return;
    let active = true;

    fetchFilterRequest(resultId)
      .then(info => {
        if (!active) return;
        const req = requestFromInfo(info);   // the server's copy wins over what the URL says
        setBarInitial(req);
        setBarKey(k => k + 1);
        if (info.status === 'done') {
          setApplied(appliedFrom(resultId, req));
          setCalcState('done');
        } else if (info.status === 'error') {
          setCalcState('error');
        } else {
          startPolling(resultId, req);
        }
      })
      .catch(e => {
        if (!active) return;
        // A refused sign-in has already ended the session (apiFetch), and the login page brings
        // the user back to this same link.
        if (e instanceof FilterRequestError && (e.status === 401 || e.status === 403)) return;
        // Gone or unreadable: keep the filter in the bar so it can be applied again, drop the id.
        setRestoreNote(e instanceof FilterRequestError && e.status === 404
          ? 'The saved result in this link no longer exists. Its filter is filled in below: press Apply Filter to calculate it again.'
          : 'The saved result in this link could not be loaded. Press Apply Filter to calculate it again.');
        setBarInitial(fromUrl);
        setBarKey(k => k + 1);
        const rest = new URLSearchParams(searchParams);
        rest.delete('result');
        setSearchParams(rest, { replace: true });
      })
      .finally(() => { if (active) setRestoring(false); });

    return () => { active = false; };
    // Deliberately once, on open: later URL changes are the page's own writes.
    // eslint-disable-next-line react-hooks/exhaustive-deps
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
        key={barKey}
        initial={barInitial}
        calcState={calcState}
        appliedContext={applied}
        onApply={handleApply}
        onClear={handleClear}
      />

      {restoreNote && <Alert severity="info" sx={{ mb: 2 }}>{restoreNote}</Alert>}

      {/* ══════════ SHARED PARAMETERS — Section 1 live, or Section 2 once filtered ══════════ */}
      {restoring ? (
        <Box display="flex" justifyContent="center" py={6}><CircularProgress /></Box>
      ) : showSection2 ? (
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
