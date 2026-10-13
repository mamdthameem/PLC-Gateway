import type { SpareStatus } from '../types';

import { apiFetch } from './apiClient';

export async function fetchSpareStatus(): Promise<SpareStatus[]> {
  const res = await apiFetch(`/api/sparestatus`);
  if (!res.ok) throw new Error(`Spare status fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<SpareStatus[]>;
}

export async function fetchSpareAlerts(): Promise<SpareStatus[]> {
  const res = await apiFetch(`/api/sparestatus/alerts`);
  if (!res.ok) throw new Error(`Spare alerts fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<SpareStatus[]>;
}
