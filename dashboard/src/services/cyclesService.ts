import type { LatestCycle, CycleAmp } from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return { ...(isJwt ? { Authorization: `Bearer ${token}` } : {}) };
}

export async function fetchLatestCycle(): Promise<LatestCycle | null> {
  const res = await fetch(`${API_BASE}/api/cycles/latest`, { headers: authHeaders() });
  if (res.status === 404) return null;
  if (!res.ok) throw new Error(`Cycles fetch failed: ${res.status}`);
  return res.json() as Promise<LatestCycle>;
}


/**
 * Average current per completed cycle for one impeller, across the WHOLE recorded history.
 *
 * The amps chart's "all cycles" range. Per-second samples cannot cover a month — a single impeller
 * holds ~178 000 of them — so the full range is served one point per cycle and the per-sample trace
 * stays on the bounded ranges.
 */
export async function fetchPerCycleAmps(impeller: number): Promise<CycleAmp[]> {
  const res = await fetch(`${API_BASE}/api/amps/by-cycle?impeller=${impeller}`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Per-cycle amps fetch failed: ${res.status}`);
  return res.json() as Promise<CycleAmp[]>;
}
