import type { LatestCycle } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

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
