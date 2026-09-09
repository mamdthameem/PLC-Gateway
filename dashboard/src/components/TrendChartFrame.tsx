import type { ReactNode } from 'react';
import { Box, CircularProgress, Alert, Typography } from '@mui/material';
import { ResponsiveContainer } from 'recharts';
import { CHART_HEIGHT, BUCKET_ADJECTIVE } from '../utils/chartAxis';
import type { TrendSeriesState } from '../utils/useTrendSeries';

interface Props {
  state: TrendSeriesState;
  emptyMessage: string;
  children: ReactNode;
}

/**
 * Loading / error / empty handling plus the range line, shared by every trend chart.
 *
 * The line states WHAT is plotted — the span the series covers and how finely it is cut — rather
 * than how the chart is built. It used to explain gap-filling and per-point mechanics, then tack
 * a lowercase formula fragment onto the end of a finished sentence; the formula now lives behind
 * the info icon next to the dialog title, where it can be written as prose.
 *
 * The dates come from the series itself, so an all-time chart shows the first and last day the
 * machine actually recorded, and a filtered chart shows the filter window.
 */
export default function TrendChartFrame({ state, emptyMessage, children }: Props) {
  const { points, bucket, loading, error } = state;

  if (loading) {
    return (
      <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: CHART_HEIGHT }}>
        <CircularProgress />
      </Box>
    );
  }
  if (error) return <Alert severity="error">{error}</Alert>;
  if (!points.length) return <Typography color="text.secondary">{emptyMessage}</Typography>;

  const stamp = (iso: string) =>
    new Date(iso).toLocaleDateString(undefined, { day: 'numeric', month: 'short', year: 'numeric' });

  const from = stamp(points[0].day);
  const to   = stamp(points[points.length - 1].day);

  return (
    <Box>
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: 1 }}>
        {from === to ? from : `${from} to ${to}`} · {BUCKET_ADJECTIVE[bucket]}
      </Typography>

      <Box sx={{ width: '100%', height: CHART_HEIGHT }}>
        <ResponsiveContainer>
          {children as React.ReactElement}
        </ResponsiveContainer>
      </Box>
    </Box>
  );
}
