import { useState, useEffect } from 'react';
import {
  Box, Paper, Typography, CircularProgress, Alert, Chip,
  Dialog, DialogTitle, DialogContent, IconButton, Tooltip,
} from '@mui/material';
import BarChartIcon from '@mui/icons-material/BarChart';
import CloseIcon from '@mui/icons-material/Close';
import { fetchAmpReadings, fetchLastCycleAmps } from '../services/ampsService';
import AmpsGraph from './AmpsGraph';
import { usePlcConnection } from '../utils/usePlcConnection';
import type { AmpReading } from '../types';

const POLL_MS = 1000;
/** The last-cycle average only moves when a cycle completes, so it does not need a 1 s poll. */
const LAST_CYCLE_POLL_MS = 30_000;
/** Below this the impellers are not turning — the machine is between loads. */
const RUNNING_THRESHOLD_A = 1.0;

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
  const [lastCycle, setLastCycle] = useState<Record<string, number>>({});
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

  // The figure each impeller last RAN at. Between cycles the live reading is a correct 0 A, which
  // tells a reader nothing about the machine — so every tile also carries this, and a stopped
  // impeller shows it as the headline with the live zero underneath.
  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const data = await fetchLastCycleAmps();
        if (!active) return;
        const map: Record<string, number> = {};
        for (const r of data) {
          const v = parseFloat(r.value);
          if (isFinite(v)) map[r.parameterName] = v;
        }
        setLastCycle(map);
      } catch {
        // Non-fatal: the tiles still show the live reading without it.
      }
    }
    load();
    const id = setInterval(load, LAST_CYCLE_POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, []);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;

  const cols = readings.length;

  return (
    <Box>
      <Typography variant="h6" mb={connected ? 2 : 0.5}>Impeller Current</Typography>

      {!connected && (
        <Alert severity="warning" sx={{ mb: 2, py: 0.25 }}>
          PLC disconnected. Showing the last values read
          {lastScanAt ? ` at ${new Date(lastScanAt).toLocaleString()}` : ''}. These are not live.
        </Alert>
      )}

      {/* Ten impellers, five to a row — a 6-column grid split them 6 + 4, which reads as though
          the last four are a different group. CSS grid rather than MUI Grid because a 12-column
          system cannot divide into fifths. */}
      <Box
        sx={{
          display: 'grid',
          // CENTRED, and width-capped rather than 1fr. The cap is what makes centring possible at
          // all: a 1fr track always fills its container, so two impellers would stretch to half
          // the screen each and justifyContent would have nothing left to centre.
          // Ten impellers still lay out five to a row.
          gridTemplateColumns: {
            xs: `repeat(${Math.min(cols, 2)}, minmax(0, 1fr))`,
            sm: `repeat(${Math.min(cols, 3)}, 200px)`,
            md: `repeat(${Math.min(cols, 5)}, 200px)`,
          },
          justifyContent: 'center',
          gap: 2,
        }}
      >
        {readings.map(r => {
          const amps    = parseFloat(r.value);
          const impNum  = impellerNumber(r.parameterName);
          const avg     = lastCycle[r.parameterName];
          const running = isFinite(amps) && amps >= RUNNING_THRESHOLD_A;

          // The headline is ALWAYS the live reading, including the 0 A of an idle machine. It
          // briefly showed the last cycle's average instead, from when the demo dataset ended at a
          // permanent shutdown and ten zeroes were all anyone saw. With the machine actually
          // cycling that would misreport a stopped impeller as drawing 19 A. The average moved to
          // the line underneath, where it is context for a zero rather than a substitute for it.
          const headline = isFinite(amps) ? amps.toFixed(2) : r.value;
          return (
            <Box key={r.parameterName}>
              {/* The whole tile opens the history chart, matching ExpandableMetricCard — the
                  icon alone was too small a target and gave no hint the card was clickable. */}
              <Paper
                variant="outlined"
                onClick={() => setOpenImp(impNum)}
                sx={{
                  p: 1.75,
                  borderRadius: 2,
                  position: 'relative',
                  cursor: 'pointer',
                  // A fixed height keeps every tile identical whether it is showing a timestamp or
                  // an Idle chip underneath, so a row of them does not sit on a ragged baseline.
                  minHeight: 104,
                  display: 'flex',
                  flexDirection: 'column',
                  justifyContent: 'space-between',
                  transition: 'box-shadow 0.15s, border-color 0.15s',
                  borderColor: running ? 'primary.light' : undefined,
                  '&:hover': { boxShadow: 4 },
                }}
              >
                <Box display="flex" alignItems="center" justifyContent="space-between">
                  <Typography
                    variant="caption"
                    color="text.secondary"
                    sx={{ fontWeight: 700, fontSize: '0.68rem', letterSpacing: '0.04em' }}
                  >
                    {impellerLabel(r.parameterName)}
                  </Typography>
                  <Tooltip title="View history">
                    <BarChartIcon sx={{ fontSize: '0.9rem', color: 'text.disabled' }} />
                  </Tooltip>
                </Box>
                <Box sx={{ display: 'flex', alignItems: 'baseline', justifyContent: 'center', gap: 0.5, mt: 1 }}>
                  <Typography
                    sx={{
                      fontWeight: 700,
                      color: running ? 'primary.main' : 'text.secondary',
                      fontSize: '1.6rem',
                      lineHeight: 1.1,
                    }}
                  >
                    {headline}
                  </Typography>
                  <Typography
                    sx={{ fontWeight: 600, color: 'text.disabled', fontSize: '0.85rem' }}
                  >
                    A
                  </Typography>
                </Box>
                {running ? (
                  <Typography
                    variant="caption"
                    color="text.disabled"
                    sx={{ fontSize: '0.6rem', display: 'block', textAlign: 'center', lineHeight: 1.3 }}
                  >
                    {new Date(r.lastUpdated).toLocaleTimeString()}
                  </Typography>
                ) : (
                  /* The headline above is the LIVE reading, so this line must not claim to label
                     it. It reports what the impeller last ran at: context for a zero, not a
                     substitute for it. */
                  <Box display="flex" alignItems="center" justifyContent="center" gap={0.5} mt={0.25}>
                    {avg != null && (
                      <Typography
                        variant="caption"
                        color="text.disabled"
                        sx={{ fontSize: '0.6rem', lineHeight: 1.3 }}
                      >
                        ran at {avg.toFixed(1)} A
                      </Typography>
                    )}
                    <Chip
                      label="Idle"
                      size="small"
                      variant="outlined"
                      sx={{ height: 15, fontSize: '0.55rem', '& .MuiChip-label': { px: 0.6 } }}
                    />
                  </Box>
                )}
              </Paper>
            </Box>
          );
        })}
      </Box>

      <Dialog
        open={openImp !== null}
        onClose={closeDialog}
        maxWidth="lg"
        fullWidth
        slotProps={{ transition: { onEntered: () => setChartReady(true) } }}
      >
        <DialogTitle sx={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
          Impeller {openImp}: Average Current per Cycle (A)
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
