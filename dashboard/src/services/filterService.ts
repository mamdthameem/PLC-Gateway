import type {
  FilterRequest, FilterStatus, FilterResult, FilteredCycle,
  FilteredMetalProduction, FilteredAmps, FilterRequestInfo,
} from '../types';

import { apiFetch } from './apiClient';

export async function submitFilterRequest(req: FilterRequest): Promise<number> {
  const res = await apiFetch(`/api/filter`, {
    method: 'POST',
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Filter submit failed: ${res.status} ${res.statusText}`);
  const { requestId } = (await res.json()) as { requestId: number };
  return requestId;
}

/** A failed request lookup, keeping the HTTP status so the page can tell "gone" from "signed out". */
export class FilterRequestError extends Error {
  status: number;
  constructor(message: string, status: number) {
    super(message);
    this.status = status;
  }
}

/**
 * One saved request by id - to reopen the result named in the URL without recomputing it.
 * Requires a signed-in user (the API answers 401 otherwise).
 */
export async function fetchFilterRequest(requestId: number): Promise<FilterRequestInfo> {
  const res = await apiFetch(`/api/filter/${requestId}`);
  if (!res.ok) throw new FilterRequestError(`Request ${requestId} lookup failed: ${res.status}`, res.status);
  return res.json() as Promise<FilterRequestInfo>;
}

export async function pollFilterStatus(requestId: number): Promise<FilterStatus> {
  const res = await apiFetch(`/api/filter/${requestId}/status`);
  if (!res.ok) throw new Error(`Status poll failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilterStatus>;
}

export async function fetchFilterResults(requestId: number): Promise<FilterResult[]> {
  const res = await apiFetch(`/api/filter/${requestId}/results`);
  if (!res.ok) throw new Error(`Results fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilterResult[]>;
}

export async function fetchFilterCycles(requestId: number): Promise<FilteredCycle[]> {
  const res = await apiFetch(`/api/filter/${requestId}/cycles`);
  if (!res.ok) throw new Error(`Cycles fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilteredCycle[]>;
}

/**
 * Section 2 production per casting metal — summed DECLARED casting-metal weights for the
 * filtered scope. Section 2 reports production this way; Section 1 reports it from the Tonnage
 * accumulator instead, so the two figures answer different questions and need not match.
 */
export async function fetchFilterMetals(requestId: number): Promise<FilteredMetalProduction[]> {
  const res = await apiFetch(`/api/filter/${requestId}/metals`);
  if (!res.ok) throw new Error(`Metal production fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilteredMetalProduction[]>;
}

// fetchFilterShots was removed along with GET /api/filter/{id}/shots: the shots breakdown is
// Section 1 only now (it does not respond to a filter), so the dashboard reads it from
// shotsBreakdownService above the filter bar instead.

/**
 * Section 2 impeller current — mirrors the Section 1 Amps tile/graph, scoped to this filter's
 * cycles instead of "last completed cycle".
 */
export async function fetchFilterAmps(requestId: number): Promise<FilteredAmps[]> {
  const res = await apiFetch(`/api/filter/${requestId}/amps`);
  if (!res.ok) throw new Error(`Filter amps fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilteredAmps[]>;
}
