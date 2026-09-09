import { useState, useEffect } from 'react';
import { fetchTrends } from '../services/trendsService';
import { formatBucketLabel, formatBucketFull } from './trendBuckets';
import type { DailyTrend, TrendBucket } from '../types';

export interface TrendPoint extends DailyTrend {
  /** Short axis label for this bucket. */
  label: string;
  /** Full interval description, for the tooltip. */
  full: string;
}

export interface TrendSeriesState {
  points: TrendPoint[];
  bucket: TrendBucket;
  loading: boolean;
  error: string | null;
}

/**
 * Fetches one bucketed trend series and decorates it with axis labels.
 *
 * Every trend chart on the dashboard uses this, so they all share one fetch shape, one label
 * format and one granularity decision. That is the point: five charts each choosing their own
 * bucket is how two graphs of the same window ended up with different period intervals.
 *
 * Omit both bounds for the all-time series. The granularity is resolved server-side and returned
 * with the rows, so the axis can be titled with the interval it is actually showing.
 */
export function useTrendSeries(windowStart?: string, windowEnd?: string): TrendSeriesState {
  const [points, setPoints]   = useState<TrendPoint[]>([]);
  const [bucket, setBucket]   = useState<TrendBucket>('day');
  const [loading, setLoading] = useState(true);
  const [error, setError]     = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    setLoading(true);
    setError(null);

    const start = windowStart ? new Date(windowStart) : undefined;
    const end   = windowEnd   ? new Date(windowEnd)   : undefined;

    fetchTrends('auto', start, end)
      .then(series => {
        if (!active) return;
        setBucket(series.bucket);
        setPoints(series.rows.map(r => ({
          ...r,
          label: formatBucketLabel(r.day, series.bucket),
          full:  formatBucketFull(r.day, series.bucket),
        })));
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });

    return () => { active = false; };
  }, [windowStart, windowEnd]);

  return { points, bucket, loading, error };
}
