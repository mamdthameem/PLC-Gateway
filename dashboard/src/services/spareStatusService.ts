import type { SpareStatus } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return {
    ...(isJwt ? { Authorization: `Bearer ${token}` } : {}),
  };
}

export async function fetchSpareStatus(): Promise<SpareStatus[]> {
  const res = await fetch(`${API_BASE}/api/sparestatus`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Spare status fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<SpareStatus[]>;
}

export async function fetchSpareAlerts(): Promise<SpareStatus[]> {
  const res = await fetch(`${API_BASE}/api/sparestatus/alerts`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Spare alerts fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<SpareStatus[]>;
}
