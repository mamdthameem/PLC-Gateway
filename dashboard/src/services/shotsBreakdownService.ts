import type { ShotsBreakdownEntry } from '../types';

import { apiFetch } from './apiClient';

export async function fetchShotsBreakdown(): Promise<ShotsBreakdownEntry[]> {
  const res = await apiFetch(`/api/shotsbreakdown`);
  if (!res.ok) throw new Error(`Shots breakdown fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<ShotsBreakdownEntry[]>;
}
