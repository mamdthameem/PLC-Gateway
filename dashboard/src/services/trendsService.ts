import type { DailyTrend, TrendBucket } from '../types';

const API_BASE = (import.meta.env.VITE_API_URL as string) || '';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return { ...(isJwt ? { Authorization: `Bearer ${token}` } : {}) };
}

/**
 * Pre-aggregated trend buckets from the plc_daily_trends rollup.
 *
 * Call with no bounds for the all-time series — that is the normal Section 1 case. The payload
 * is one row per day (or per month), so an all-time request stays small however long the plant
 * has been recording; the raw Tier 2 history is never shipped to the browser.
 */
export async function fetchTrends(
  bucket: TrendBucket = 'day',
  start?: Date,
  end?: Date
): Promise<DailyTrend[]> {
  const params = new URLSearchParams({ bucket });
  if (start) params.set('start', start.toISOString());
  if (end)   params.set('end',   end.toISOString());

  const res = await fetch(`${API_BASE}/api/trends?${params}`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Trends fetch failed: ${res.status} ${res.statusText}`);
  return res.json() as Promise<DailyTrend[]>;
}
