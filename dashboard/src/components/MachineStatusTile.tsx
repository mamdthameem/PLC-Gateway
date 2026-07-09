import { useState, useEffect } from 'react';
import { Paper, Typography, Chip, Box } from '@mui/material';
import { fetchMachineStatus } from '../services/machineStatusService';

const POLL_MS = 5000;

export default function MachineStatusTile() {
  const [value, setValue] = useState<string | null>(null);
  const [updatedAt, setUpdatedAt] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const s = await fetchMachineStatus();
        if (active) { setValue(s.value); setUpdatedAt(s.lastUpdated); }
      } catch {
        // silent — show last known value
      }
    }
    load();
    const id = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, []);

  const running = value !== null && value !== '0';
  const label   = value === null ? '—' : running ? 'Running' : 'Stopped';

  return (
    <Paper sx={{ p: 2.5, borderRadius: 2, display: 'flex', flexDirection: 'column', gap: 0.5 }}>
      <Typography
        variant="caption"
        sx={{ color: 'text.secondary', fontWeight: 600, letterSpacing: '0.07em', fontSize: '0.68rem', textTransform: 'uppercase' }}
      >
        Machine Status
      </Typography>
      <Box mt={0.5}>
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
      </Box>
      {updatedAt && (
        <Typography variant="caption" sx={{ color: 'text.disabled', fontSize: '0.62rem', mt: 'auto' }}>
          {new Date(updatedAt).toLocaleTimeString()}
        </Typography>
      )}
    </Paper>
  );
}
