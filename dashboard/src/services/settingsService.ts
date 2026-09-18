import type { ImpellerSelection } from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return {
    ...(isJwt ? { Authorization: `Bearer ${token}` } : {}),
  };
}

/**
 * Fired on `window` after an impeller selection is saved. Panels that poll slowly (the lifetime
 * tiles every 60 s, spare health every 10 s) listen for it and reload at once, so the recalculated
 * energy and the new set of impellers appear together.
 */
export const IMPELLER_SELECTION_CHANGED = 'impeller-selection-changed';

export async function fetchImpellerSelection(): Promise<ImpellerSelection> {
  const res = await fetch(`${API_BASE}/api/settings/impellers`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Impeller selection fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<ImpellerSelection>;
}

/**
 * Saves the selection for every viewer. Resolves only once the gateway has recalculated the energy
 * of every recorded cycle, so the figures are already updated when it returns.
 */
export async function saveImpellerSelection(selected: number[]): Promise<ImpellerSelection> {
  const res = await fetch(`${API_BASE}/api/settings/impellers`, {
    method: 'PUT',
    headers: { ...authHeaders(), 'Content-Type': 'application/json' },
    body: JSON.stringify({ selected }),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => null) as { error?: string } | null;
    throw new Error(body?.error ?? `Saving the impeller selection failed: ${res.status} ${res.statusText}`);
  }
  const saved = await res.json() as ImpellerSelection;
  window.dispatchEvent(new Event(IMPELLER_SELECTION_CHANGED));
  return saved;
}
