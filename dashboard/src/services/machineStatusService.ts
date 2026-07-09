import type { MachineStatus } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return { ...(isJwt ? { Authorization: `Bearer ${token}` } : {}) };
}

export async function fetchMachineStatus(): Promise<MachineStatus> {
  const res = await fetch(`${API_BASE}/api/machinestatus`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Machine status fetch failed: ${res.status}`);
  return res.json() as Promise<MachineStatus>;
}
