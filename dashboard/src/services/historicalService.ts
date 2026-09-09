import type { HistoricalPoint } from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return { ...(isJwt ? { Authorization: `Bearer ${token}` } : {}) };
}

export async function fetchHistorical(
  parameterName: string,
  start: Date,
  end: Date
): Promise<HistoricalPoint[]> {
  const params = new URLSearchParams({
    name:  parameterName,
    start: start.toISOString(),
    end:   end.toISOString(),
  });
  const res = await fetch(`${API_BASE}/api/historical?${params}`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Historical fetch failed for ${parameterName}: ${res.status}`);
  return res.json() as Promise<HistoricalPoint[]>;
}
