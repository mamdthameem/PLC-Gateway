import { useState, useEffect } from 'react';
import {
  Box, Grid, Paper, Typography, CircularProgress, Alert,
  Dialog, DialogTitle, DialogContent, IconButton, Tooltip,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import { fetchFilterAmps } from '../services/filterService';
import FilteredAmpsGraph from './FilteredAmpsGraph';
import type { FilteredAmps } from '../types';

interface Props {
  requestId: number;
}

/**
 * Section 2 counterpart to AmpsPanel — same tile-grid-plus-dialog shape, but the tile shows a
 * duration-weighted average over this filter's cycles instead of a live reading, and the dialog
 * chart is scoped to this filter instead of "Last Blast Cycle".
 */
export default function FilteredAmpsPanel({ requestId }: Props) {
  const [readings, setReadings] = useState<FilteredAmps[]>([]);
  const [error, setError]       = useState<string | null>(null);
  const [loading, setLoading]   = useState(true);
  const [openImp, setOpenImp]   = useState<number | null>(null);

  // See ExpandableMetricCard: a Recharts ResponsiveContainer mounted mid-dialog-transition can
  // measure zero width and render an empty box, so wait until the dialog has finished opening.
  const [chartReady, setChartReady] = useState(false);
  const closeDialog = () => { setOpenImp(null); setChartReady(false); };

  useEffect(() => {
    let active = true;
    setLoading(true);
    fetchFilterAmps(requestId)
      .then(data => {
        if (!active) return;
        setReadings([...data].sort((a, b) => a.impellerNumber - b.impellerNumber));
        setError(null);
        setLoading(false);
      })
      .catch(e => {
        if (!active) return;
        setError((e as Error).message);
        setLoading(false);
      });
    return () => { active = false; };
  }, [requestId]);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;
  if (!readings.length) return <Alert severity="info">No completed blast cycles fall within this filter.</Alert>;

  return (
    <Box>
      <Grid container spacing={2}>
        {readings.map(r => {
          const display = r.overallAvgAmps != null ? `${r.overallAvgAmps.toFixed(2)} A` : '—';
          return (
            <Grid key={r.impellerNumber} size={{ xs: 6, sm: 4, md: 2 }}>
              <Paper
                variant="outlined"
                onClick={() => setOpenImp(r.impellerNumber)}
                sx={{
                  p: 1.5,
                  borderRadius: 2,
                  position: 'relative',
                  cursor: 'pointer',
                  transition: 'box-shadow 0.15s',
                  '&:hover': { boxShadow: 4 },
                }}
              >
                <Box display="flex" alignItems="center" justifyContent="space-between">
                  <Typography
                    variant="caption"
                    color="text.secondary"
                    sx={{ fontWeight: 600, fontSize: '0.65rem', textTransform: 'uppercase' }}
                  >
                    Impeller {r.impellerNumber}
                  </Typography>
                  <Tooltip title="View trend for this filter">
                    <BarChartIcon sx={{ fontSize: '0.9rem', color: 'text.disabled' }} />
                  </Tooltip>
                </Box>
                <Typography
                  variant="h6"
                  sx={{ fontWeight: 700, color: 'primary.main', fontSize: '1.1rem', textAlign: 'center', mt: 0.5 }}
                >
                  {display}
                </Typography>
                <Typography variant="caption" color="text.disabled" sx={{ fontSize: '0.6rem', display: 'block', textAlign: 'center' }}>
                  avg for this filter
                </Typography>
              </Paper>
            </Grid>
          );
        })}
      </Grid>

      <Dialog
        open={openImp !== null}
        onClose={closeDialog}
        maxWidth="md"
        fullWidth
        slotProps={{ transition: { onEntered: () => setChartReady(true) } }}
      >
        <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          Impeller {openImp} — Current (A) · This Filter
          <IconButton onClick={closeDialog} size="small"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent>
          {openImp !== null && chartReady ? (
            <FilteredAmpsGraph requestId={requestId} impellerNumber={openImp} />
          ) : (
            <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', height: 300 }}>
              <CircularProgress />
            </Box>
          )}
        </DialogContent>
      </Dialog>
    </Box>
  );
}
