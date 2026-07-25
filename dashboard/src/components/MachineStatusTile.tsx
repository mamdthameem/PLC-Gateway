import { useState, useEffect } from 'react';
import { Paper, Typography, Chip, Box, Tooltip } from '@mui/material';
import LinkOffIcon from '@mui/icons-material/LinkOff';
import { fetchMachineStatus } from '../services/machineStatusService';
import type { MachineStatus } from '../types';

const POLL_MS = 5000;

export default function MachineStatusTile() {
  const [status, setStatus]     = useState<MachineStatus | null>(null);
  const [fetchFailed, setFailed] = useState(false);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const s = await fetchMachineStatus();
        if (active) { setStatus(s); setFailed(false); }
      } catch {
        // Keep showing the last known value, but stop implying it is current.
        if (active) setFailed(true);
      }
    }
    load();
    const id = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, []);

  const value   = status?.value ?? null;
  const running = value !== null && value !== '0';
  const label   = value === null ? '—' : running ? 'Running' : 'Stopped';

  // When the PLC link drops, the backend forces the machine value to 0 and flags the row stale,
  // so "Stopped" is authoritative rather than a stale reading. Say why, otherwise a disconnected
  // gateway is indistinguishable from a genuinely idle machine.
  const disconnected = status !== null && !status.plcConnected;

  return (
    <Paper sx={{ p: 2.5, borderRadius: 2, display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <Typography
        variant="caption"
        sx={{ color: 'text.secondary', fontWeight: 600, letterSpacing: '0.07em', fontSize: '0.68rem', textTransform: 'uppercase' }}
      >
        Machine Status
      </Typography>

      <Box mt={0.5} display="flex" alignItems="center" gap={1} flexWrap="wrap">
        <Chip
          label={label}
          size="small"
          sx={{
            fontWeight: 700,
            fontSize: '0.85rem',
            backgroundColor: running ? 'rgba(34,197,94,0.15)' : 'rgba(239,68,68,0.15)',
            color: running ? '#22c55e' : '#ef4444',
            border: `1px solid ${running ? '#22c55e' : '#ef4444'}`,
          }}
        />
        {disconnected && (
          <Tooltip
            title={
              status?.lastScanAt
                ? `Last successful PLC scan: ${new Date(status.lastScanAt).toLocaleString()}`
                : 'No successful PLC scan recorded yet'
            }
          >
            <Chip
              icon={<LinkOffIcon sx={{ fontSize: '0.85rem' }} />}
              label="PLC Disconnected"
              size="small"
              color="warning"
              variant="outlined"
              sx={{ fontWeight: 600, fontSize: '0.68rem' }}
            />
          </Tooltip>
        )}
      </Box>

      {disconnected && (
        <Typography variant="caption" sx={{ color: 'warning.main', fontSize: '0.62rem' }}>
          Machine reported OFF because the gateway cannot reach the PLC. Recording continues.
        </Typography>
      )}

      {fetchFailed && (
        <Typography variant="caption" sx={{ color: 'error.main', fontSize: '0.62rem' }}>
          Cannot reach the gateway API — value below may be out of date.
        </Typography>
      )}

      {status?.lastUpdated && (
        <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', mt: 'auto' }}>
          {new Date(status.lastUpdated).toLocaleTimeString()}
        </Typography>
      )}
    </Paper>
  );
}
