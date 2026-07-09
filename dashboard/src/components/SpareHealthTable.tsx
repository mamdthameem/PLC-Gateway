import { useState, useEffect, useRef } from 'react';
import {
  Box, Table, TableBody, TableCell, TableContainer, TableHead, TableRow,
  Paper, Typography, CircularProgress, Alert, Snackbar, Chip,
} from '@mui/material';
import { fetchSpareStatus } from '../services/spareStatusService';
import type { SpareStatus } from '../types';
import { formatRunHours } from '../utils/unitConverters';

const POLL_MS          = 10_000;
const POPUP_COOLDOWN   = 30 * 60 * 1000; // 30 minutes
const IMPELLER_COUNT   = 10;

interface PopupMsg { key: string; text: string }

export default function SpareHealthTable() {
  const [rows, setRows]         = useState<SpareStatus[]>([]);
  const [error, setError]       = useState<string | null>(null);
  const [loading, setLoading]   = useState(true);
  const [queue, setQueue]       = useState<PopupMsg[]>([]);
  const [shown, setShown]       = useState<PopupMsg | null>(null);

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
              `Maintenance required — Impeller ${d.impellerNum}, ${d.spareName}: ` +
              `${formatRunHours(d.currentRunHours)} run (threshold ${formatRunHours(d.thresholdHours)})`
            );
          }

          // Replacement confirmation (last_replaced_at within last 24 hours)
          if (d.lastReplacedAt && new Date(d.lastReplacedAt).getTime() >= dayAgo) {
            maybeEnqueue(
              replKey,
              `Spare replaced — Impeller ${d.impellerNum}, ${d.spareName}. ` +
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
  const impellers  = Array.from({ length: IMPELLER_COUNT }, (_, i) => i + 1);
  const cell       = (imp: number, spare: string) =>
    rows.find(r => r.impellerNum === imp && r.spareName === spare);

  if (loading) return <Box sx={{ display: 'flex', justifyContent: 'center', py: 3 }}><CircularProgress /></Box>;
  if (error)   return <Alert severity="error">{error}</Alert>;

  return (
    <Box>
      <Typography variant="h6" sx={{ mb: 2 }}>Spare Parts Health</Typography>

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

      <TableContainer component={Paper} variant="outlined" sx={{ overflowX: 'auto' }}>
        <Table size="small" stickyHeader>
          <TableHead>
            <TableRow>
              <TableCell sx={{ fontWeight: 700, minWidth: 150 }}>Spare Part</TableCell>
              {impellers.map(i => (
                <TableCell key={i} align="center" sx={{ fontWeight: 700, minWidth: 110 }}>
                  Imp {i}
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
