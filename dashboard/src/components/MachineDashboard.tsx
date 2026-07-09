import React, { useState, useRef, useCallback } from 'react';
import { Container, Divider } from '@mui/material';
import FilterBar, { type FilterCalcState } from './FilterBar';
import MachineStatusTile from './MachineStatusTile';
import { LifetimeSection } from './LifetimeSection';
import AmpsPanel from './AmpsPanel';
import SpareHealthTable from './SpareHealthTable';
import FilterResultsView from './FilterResultsView';
import { submitFilterRequest, pollFilterStatus } from '../services/filterService';
import type { FilterRequest } from '../types';

interface AppliedContext {
  requestId: number;
  label: string;
  filterStart: string;
  filterEnd: string;
  filterBy: 'time' | 'cycle' | 'metal';
}

function filterLabel(req: FilterRequest): string {
  if (req.filterBy === 'cycle') return `Cycles ${req.filterCycleFrom}–${req.filterCycleTo}`;
  if (req.filterBy === 'metal') return `Metal: ${req.filterMetalName}`;
  if (req.periodLabel) return req.periodLabel.charAt(0).toUpperCase() + req.periodLabel.slice(1) + ' view';
  return 'Custom range';
}

export const MachineDashboard: React.FC = () => {
  const [calcState, setCalcState]       = useState<FilterCalcState>('idle');
  const [applied, setApplied]           = useState<AppliedContext | null>(null);
  const pollRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const stopPoll = () => {
    if (pollRef.current) { clearInterval(pollRef.current); pollRef.current = null; }
  };

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

  const handleClear = useCallback(() => {
    stopPoll();
    setCalcState('idle');
    setApplied(null);
  }, []);

  return (
    <Container maxWidth="xl" sx={{ py: 3 }}>
      {/* Global filter bar — always at the top */}
      <FilterBar
        calcState={calcState}
        appliedContext={applied}
        onApply={handleApply}
        onClear={handleClear}
      />

      {calcState === 'done' && applied ? (
        /* ── Section 2 — filtered view ── */
        <FilterResultsView
          requestId={applied.requestId}
          filterStart={applied.filterStart}
          filterEnd={applied.filterEnd}
          filterBy={applied.filterBy}
          label={applied.label}
        />
      ) : (
        /* ── Section 1 — live / all-time view ── */
        <>
          {/* Machine status — 5 s poll, Section 1 only */}
          <MachineStatusTile />

          <Divider sx={{ my: 3 }} />

          {/* Lifetime scalar params + shots usage tile + shots chart */}
          <LifetimeSection />

          <Divider sx={{ my: 3 }} />

          {/* Live amps — 1 s poll, Section 1 only */}
          <AmpsPanel />

          <Divider sx={{ my: 3 }} />

          {/* Spare health grid — 10 s poll, Section 1 only */}
          <SpareHealthTable />
        </>
      )}
    </Container>
  );
};
