import type { AmpReading } from '../types';

import { apiFetch } from './apiClient';

export async function fetchAmpReadings(): Promise<AmpReading[]> {
  const res = await apiFetch(`/api/amps`);
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
  const res = await apiFetch(`/api/amps/last-cycle`);
  if (!res.ok) throw new Error(`Last-cycle amps fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<AmpReading[]>;
}
