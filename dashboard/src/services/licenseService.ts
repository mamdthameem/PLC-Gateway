import type { LicenseStatus } from '../types';

import { API_BASE } from './apiBase';

/** Licence state for the lock screen. Anonymous, and never locked itself by the licence check. */
export async function fetchLicenseStatus(): Promise<LicenseStatus> {
  const res = await fetch(`${API_BASE}/api/license`);
  if (!res.ok) throw new Error(`License status fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<LicenseStatus>;
}
