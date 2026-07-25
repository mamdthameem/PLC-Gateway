import { useState, useEffect } from 'react';
import {
  Box, Grid, Paper, Typography, CircularProgress, Alert,
  Dialog, DialogTitle, DialogContent, IconButton, Tooltip,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import { fetchAmpReadings } from '../services/ampsService';
import AmpsGraph from './AmpsGraph';
import { usePlcConnection } from '../utils/usePlcConnection';
import type { AmpReading } from '../types';

const POLL_MS = 1000;

function impellerNumber(paramName: string): number {
  const m = paramName.match(/(\d+)$/);
  return m ? parseInt(m[1], 10) : 0;
}

function impellerLabel(paramName: string): string {
  const n = impellerNumber(paramName);
  return n ? `Impeller ${n}` : paramName;
}

export default function AmpsPanel() {
  const [readings, setReadings]   = useState<AmpReading[]>([]);
  const [error, setError]         = useState<string | null>(null);
  const [loading, setLoading]     = useState(true);
  const [openImp, setOpenImp]     = useState<number | null>(null);
  const { connected, lastScanAt } = usePlcConnection();

  // See ExpandableMetricCard: a Recharts ResponsiveContainer mounted mid-dialog-transition can
  // measure zero width and render an empty box, so wait until the dialog has finished opening.
  const [chartReady, setChartReady] = useState(false);
  const closeDialog = () => { setOpenImp(null); setChartReady(false); };

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const data = await fetchAmpReadings();
        if (active) { setReadings(data); setError(null); }
      } catch (e) {
        if (active) setError((e as Error).message);
      } finally {
        if (active) setLoading(false);
      }
    }
    load();
    const id = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, []);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;

  return (
    <Box>
      <Typography variant="h6" mb={connected ? 2 : 0.5}>Live Impeller Current (A)</Typography>

      {!connected && (
        <Alert severity="warning" sx={{ mb: 2, py: 0.25 }}>
          PLC disconnected — showing the last values read
          {lastScanAt ? ` at ${new Date(lastScanAt).toLocaleString()}` : ''}. These are not live.
        </Alert>
      )}

      <Grid container spacing={2}>
        {readings.map(r => {
          const amps   = parseFloat(r.value);
          const display = isFinite(amps) ? `${amps.toFixed(2)} A` : r.value;
          const impNum  = impellerNumber(r.parameterName);
          return (
            <Grid key={r.parameterName} size={{ xs: 6, sm: 4, md: 2 }}>
              {/* The whole tile opens the history chart, matching ExpandableMetricCard — the
                  icon alone was too small a target and gave no hint the card was clickable. */}
              <Paper
                variant="outlined"
                onClick={() => setOpenImp(impNum)}
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
                    {impellerLabel(r.parameterName)}
                  </Typography>
                  <Tooltip title="View history">
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
                  {new Date(r.lastUpdated).toLocaleTimeString()}
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
          Impeller {openImp} — Current (A) · Last Blast Cycle
          <IconButton onClick={closeDialog} size="small"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent>
          {openImp !== null && chartReady ? (
            <AmpsGraph impellerNumber={openImp} />
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
