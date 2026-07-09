import type { FilterRequest, FilterStatus, FilterResult, FilteredCycle, ShotsBreakdownEntry } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

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

export async function fetchFilterShots(requestId: number): Promise<ShotsBreakdownEntry[]> {
  const res = await fetch(`${API_BASE}/api/filter/${requestId}/shots`, {
    headers: authHeaders(),
  });
  if (!res.ok) throw new Error(`Filter shots fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<ShotsBreakdownEntry[]>;
}
