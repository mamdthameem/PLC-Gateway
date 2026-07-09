import type { AmpReading } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

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
