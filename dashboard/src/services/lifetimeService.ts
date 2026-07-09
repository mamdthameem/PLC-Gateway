import type { LifetimeParameter } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return {
    'Content-Type': 'application/json',
    ...(isJwt ? { Authorization: `Bearer ${token}` } : {}),
  };
}

export async function fetchLifetimeParameters(): Promise<LifetimeParameter[]> {
  const res = await fetch(`${API_BASE}/api/lifetime`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Lifetime fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<LifetimeParameter[]>;
}
