import type { DailyTrend, TrendBucket } from '../types';

import { API_BASE } from './apiBase';

function authHeaders(): Record<string, string> {
  const token = localStorage.getItem('plc_gateway_token');
  const isJwt = token && token.includes('.') && !token.startsWith('local-');
  return { ...(isJwt ? { Authorization: `Bearer ${token}` } : {}) };
}

export interface TrendSeries {
  /** The granularity the server actually used — the axis is titled from this. */
  bucket: TrendBucket;
  rows: DailyTrend[];
}

/**
 * Pre-aggregated trend buckets from the plc_daily_trends rollup.
 *
 * Call with no bounds for the all-time series — that is the normal Section 1 case. The payload is
 * one row per bucket, so an all-time request stays small however long the plant has been
 * recording; the raw Tier 2 history is never shipped to the browser.
 *
 * Pass bucket 'auto' (the default) and let the server choose. It knows the span of history that
 * actually exists; the browser does not, and the old code guessed "month" for every all-time
 * request — which turned a month of recording into two bars, one covering 19 days and one
 * covering 6. The choice comes back in X-Trend-Bucket so the axis can be titled without the
 * dashboard re-deriving it.
 *
 * The series is gap-filled server-side: an idle day is a zero row, not a missing one, so equal
 * spacing along the axis really does mean equal elapsed time.
 */
export async function fetchTrends(
  bucket: TrendBucket | 'auto' = 'auto',
  start?: Date,
  end?: Date
): Promise<TrendSeries> {
  const params = new URLSearchParams({ bucket });
  if (start) params.set('start', start.toISOString());
  if (end)   params.set('end',   end.toISOString());

  const res = await fetch(`${API_BASE}/api/trends?${params}`, { headers: authHeaders() });
  if (!res.ok) throw new Error(`Trends fetch failed: ${res.status} ${res.statusText}`);

  const rows = (await res.json()) as DailyTrend[];
  const resolved = (res.headers.get('X-Trend-Bucket') ?? 'day') as TrendBucket;

  return { bucket: resolved, rows };
}
