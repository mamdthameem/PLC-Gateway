import type {
  FilterRequest, FilterStatus, FilterResult, FilteredCycle,
  FilteredMetalProduction, FilteredAmps,
} from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return {
    'Content-Type': 'application/json',
    ...(isJwt ? { Authorization: `Bearer ${token}` } : {}),
  };
}

export async function submitFilterRequest(req: FilterRequest): Promise<number> {
  const res = await fetch(`${API_BASE}/api/filter`, {
    method: 'POST',
    headers: authHeaders(),
    body: JSON.stringify(req),
  });
  if (!res.ok) throw new Error(`Filter submit failed: ${res.status} ${res.statusText}`);
  const { requestId } = (await res.json()) as { requestId: number };
  return requestId;
}

export async function pollFilterStatus(requestId: number): Promise<FilterStatus> {
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/status`, {
    headers: authHeaders(),
  });
  if (!res.ok) throw new Error(`Status poll failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilterStatus>;
}

export async function fetchFilterResults(requestId: number): Promise<FilterResult[]> {
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/results`, {
    headers: authHeaders(),
  });
  if (!res.ok) throw new Error(`Results fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilterResult[]>;
}

export async function fetchFilterCycles(requestId: number): Promise<FilteredCycle[]> {
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/cycles`, {
    headers: authHeaders(),
  });
  if (!res.ok) throw new Error(`Cycles fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilteredCycle[]>;
}

/**
 * Section 2 production per casting metal — summed DECLARED casting-metal weights for the
 * filtered scope. Section 2 reports production this way; Section 1 reports it from the Tonnage
 * accumulator instead, so the two figures answer different questions and need not match.
 */
export async function fetchFilterMetals(requestId: number): Promise<FilteredMetalProduction[]> {
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/metals`, {
    headers: authHeaders(),
  });
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
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/amps`, {
    headers: authHeaders(),
  });
  if (!res.ok) throw new Error(`Filter amps fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<FilteredAmps[]>;
}
