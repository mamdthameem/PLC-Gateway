import type { LifetimeParameter } from '../types';

import { apiFetch } from './apiClient';

export async function fetchLifetimeParameters(): Promise<LifetimeParameter[]> {
  const res = await apiFetch(`/api/lifetime`);
  if (!res.ok) throw new Error(`Lifetime fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<LifetimeParameter[]>;
}
