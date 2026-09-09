import type { AmpReading } from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return {
    ...(isJwt ? { Authorization: `Bearer ${token}` } : {}),
  };
}

export async function fetchAmpReadings(): Promise<AmpReading[]> {
  const res = await fetch(`${API_BASE}/api/amps`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Amps fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<AmpReading[]>;
}

/**
 * Average current per impeller over the last completed blast cycle.
 *
 * Polled far more slowly than the live reading — it only changes when a cycle finishes. It is what
 * the tiles fall back to for a machine that is between loads, where the live reading is a truthful
 * but uninformative 0 A.
 */
export async function fetchLastCycleAmps(): Promise<AmpReading[]> {
  const res = await fetch(`${API_BASE}/api/amps/last-cycle`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Last-cycle amps fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<AmpReading[]>;
}
