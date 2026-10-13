import type { MachineStatus } from '../types';

import { apiFetch } from './apiClient';

export async function fetchMachineStatus(): Promise<MachineStatus> {
  const res = await apiFetch(`/api/machinestatus`);
  if (!res.ok) throw new Error(`Machine status fetch failed: ${res.status}`);
  return res.json() as Promise<MachineStatus>;
}
