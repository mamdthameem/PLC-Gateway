import type { HistoricalPoint } from '../types';

import { apiFetch } from './apiClient';

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
  const res = await apiFetch(`/api/historical?${params}`);
  if (!res.ok) throw new Error(`Historical fetch failed for ${parameterName}: ${res.status}`);
  return res.json() as Promise<HistoricalPoint[]>;
}
