import { useState, useEffect, useRef } from 'react';
import {
  Box, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  Paper, Typography, CircularProgress, Alert, Snackbar, Chip,
} from '@mui/material';
import { fetchSpareStatus } from '../services/spareStatusService';
import type { SpareStatus } from '../types';
import { formatRunHours } from '../utils/unitConverters';
import { usePlcConnection } from '../utils/usePlcConnection';

const POLL_MS          = 10_000;
const POPUP_COOLDOWN   = 30 * 60 * 1000; // 30 minutes
/** Column widths. The table's overall width is budgeted from these and the impeller count. */
const SPARE_COL_WIDTH    = 220;
const IMPELLER_COL_WIDTH = 190;

// Impeller columns are derived from the rows the API returns, never assumed. The gateway serves
// only the impellers the machine actually has (Impellers:Count), so a 2-impeller rig gets two
// columns without the dashboard being told separately.

interface PopupMsg { key: string; text: string }

export default function SpareHealthTable() {
  const [rows, setRows]         = useState<SpareStatus[]>([]);
  const [error, setError]       = useState<string | null>(null);
  const [loading, setLoading]   = useState(true);
  const [queue, setQueue]       = useState<PopupMsg[]>([]);
  const [shown, setShown]       = useState<PopupMsg | null>(null);
  const { connected, lastScanAt } = usePlcConnection();

  // Per-spare cooldown tracking (key = `${impellerNum}-${spareIndex}-${type}`)
  const lastShown = useRef<Map<string, number>>(new Map());

  function maybeEnqueue(key: string, text: string) {
    const last = lastShown.current.get(key) ?? 0;
    if (Date.now() - last < POPUP_COOLDOWN) return;
    lastShown.current.set(key, Date.now());
    setQueue(q => [...q, { key, text }]);
  }

  // Show one popup at a time from the queue
  useEffect(() => {
    if (!shown && queue.length > 0) {
      setShown(queue[0]);
      setQueue(q => q.slice(1));
    }
  }, [queue, shown]);

  useEffect(() => {
    let active = true;

    async function load() {
      try {
        const data = await fetchSpareStatus();
        if (!active) return;
        setRows(data);
        setError(null);

        const now = Date.now();
        const dayAgo = now - 24 * 3_600_000;

        for (const d of data) {
          const alertKey = `alert-${d.impellerNum}-${d.spareIndex}`;
          const replKey  = `repl-${d.impellerNum}-${d.spareIndex}`;

          // Maintenance alert
          if (d.triggerActive && d.thresholdHours > 0) {
            maybeEnqueue(
              alertKey,
              `Maintenance required, Impeller ${d.impellerNum}, ${d.spareName}: ` +
              `${formatRunHours(d.currentRunHours)} run (threshold ${formatRunHours(d.thresholdHours)})`
            );
          }

          // Replacement confirmation (last_replaced_at within last 24 hours)
          if (d.lastReplacedAt && new Date(d.lastReplacedAt).getTime() >= dayAgo) {
            maybeEnqueue(
              replKey,
              `Spare replaced, Impeller ${d.impellerNum}, ${d.spareName}. ` +
              `Run hours reset. Replaced at ${new Date(d.lastReplacedAt).toLocaleString()}`
            );
          }
        }
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

  const spareNames = Array.from(new Set(rows.map(r => r.spareName)));
  const impellers  = Array.from(new Set(rows.map(r => r.impellerNum))).sort((a, b) => a - b);
  const cell       = (imp: number, spare: string) =>
    rows.find(r => r.impellerNum === imp && r.spareName === spare);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;

  return (
    <Box>
      <Typography variant="h6" sx={{ mb: 0.25 }}>Spare Part Life</Typography>

      {/* The cells read "12.0 hrs / 300.0 hrs". One key line above the table explains the pair,
          so no cell and no column needs its own caption. */}
      <Typography variant="caption" color="text.secondary" display="block" sx={{ mb: connected ? 1.5 : 0.5 }}>
        Run hours / replacement limit
      </Typography>

      {!connected && (
        <Alert severity="warning" sx={{ mb: 2, py: 0.25 }}>
          PLC disconnected. Run hours below are the last values read
          {lastScanAt ? ` at ${new Date(lastScanAt).toLocaleString()}` : ''} and are not advancing.
        </Alert>
      )}

      {/* Queued popup (one at a time) */}
      <Snackbar
        open={shown !== null}
        autoHideDuration={8000}
        onClose={() => setShown(null)}
        anchorOrigin={{ vertical: 'top', horizontal: 'center' }}
      >
        <Alert
          severity={shown?.key.startsWith('repl') ? 'success' : 'warning'}
          onClose={() => setShown(null)}
          sx={{ width: '100%' }}
        >
          {shown?.text}
        </Alert>
      </Snackbar>

      {/* CENTRED, and sized from the number of impellers rather than shrink-wrapped to its text.

          `fit-content` was the previous rule and it made a 2-impeller table cramped: it collapsed
          to the narrowest layout the content allowed, so "260.8 hrs / 300.0 hrs" sat in a ~110 px
          column. Growing to the full page width is the opposite failure — three columns stretched
          across a wide monitor. Budgeting a width per impeller gives each cell room to breathe,
          leaves the block narrow enough for `mx: auto` to actually centre it, and still scrolls
          horizontally once there are enough impellers to overflow. */}
      <TableContainer
        component={Paper}
        variant="outlined"
        sx={{
          overflowX: 'auto',
          width: '100%',
          maxWidth: SPARE_COL_WIDTH + impellers.length * IMPELLER_COL_WIDTH,
          mx: 'auto',
        }}
      >
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 700, minWidth: SPARE_COL_WIDTH }}>Spare Part</TableCell>
              {impellers.map(i => (
                <TableCell key={i} align="center" sx={{ fontWeight: 700, minWidth: IMPELLER_COL_WIDTH }}>
                  Impeller {i}
                </TableCell>
              ))}
            </TableRow>
          </TableHead>
          <TableBody>
            {spareNames.map(spare => (
              <TableRow key={spare}>
                <TableCell sx={{ fontWeight: 600 }}>{spare}</TableCell>
                {impellers.map(i => {
                  const c = cell(i, spare);
                  if (!c) return <TableCell key={i} align="center">—</TableCell>;

                  const noThreshold = c.thresholdHours === 0;
                  const triggered   = c.triggerActive;
                  const replaced    = c.lastReplacedAt !== null;

                  const runStr = formatRunHours(c.currentRunHours);
                  const display = noThreshold
                    ? runStr
                    : `${runStr} / ${formatRunHours(c.thresholdHours)}`;

                  return (
                    <TableCell
                      key={i}
                      align="center"
                      sx={{ bgcolor: triggered ? 'error.light' : 'inherit', verticalAlign: 'middle' }}
                    >
                      <Typography
                        variant="caption"
                        display="block"
                        sx={{ fontWeight: triggered ? 700 : 400, fontSize: '0.72rem' }}
                      >
                        {display}
                      </Typography>
                      {triggered && (
                        <Chip label="!" color="error" size="small"
                          sx={{ height: 14, fontSize: 9, mt: 0.25 }} />
                      )}
                      {replaced && !triggered && (
                        <Chip label="✓" color="success" size="small"
                          sx={{ height: 14, fontSize: 9, mt: 0.25 }} />
                      )}
                    </TableCell>
                  );
                })}
              </TableRow>
            ))}
          </TableBody>
        </Table>
      </TableContainer>
    </Box>
  );
}
