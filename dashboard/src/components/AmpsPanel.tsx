import { useState, useEffect } from 'react';
import {
  Box, Grid, Paper, Typography, CircularProgress, Alert,
  Dialog, DialogTitle, DialogContent, IconButton, Tooltip,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import { fetchAmpReadings } from '../services/ampsService';
import AmpsGraph from './AmpsGraph';
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
      <Typography variant="h6" mb={2}>Live Impeller Current (A)</Typography>

      <Grid container spacing={2}>
        {readings.map(r => {
          const amps   = parseFloat(r.value);
          const display = isFinite(amps) ? `${amps.toFixed(2)} A` : r.value;
          const impNum  = impellerNumber(r.parameterName);
          return (
            <Grid key={r.parameterName} size={{ xs: 6, sm: 4, md: 2 }}>
              <Paper
                variant="outlined"
                sx={{ p: 1.5, borderRadius: 2, position: 'relative' }}
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
                    <IconButton
                      size="small"
                      sx={{ p: 0.25 }}
                      onClick={() => setOpenImp(impNum)}
                    >
                      <BarChartIcon sx={{ fontSize: '0.9rem' }} />
                    </IconButton>
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

      <Dialog open={openImp !== null} onClose={() => setOpenImp(null)} maxWidth="md" fullWidth>
        <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          Impeller {openImp} — Current (A) · Last Blast Cycle
          <IconButton onClick={() => setOpenImp(null)} size="small"><CloseIcon /></IconButton>
        </DialogTitle>
        <DialogContent>
          {openImp !== null && <AmpsGraph impellerNumber={openImp} />}
        </DialogContent>
      </Dialog>
    </Box>
  );
}
