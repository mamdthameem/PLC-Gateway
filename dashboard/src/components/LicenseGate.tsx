import { useEffect, useState } from 'react';
import type { ReactNode } from 'react';
import { Alert, Box, Paper, Typography } from '@mui/material';
import LockIcon from '@mui/icons-material/Lock';
import { fetchLicenseStatus } from '../services/licenseService';
import type { LicenseStatus } from '../types';

const POLL_MS = 30_000;

/**
 * Shows the dashboard only while the gateway's licence allows it. When the licence check locks
 * (the key was rejected, or the licence server has been silent for longer than the grace period)
 * the dashboard is swapped for a lock screen — which also stops every panel's polling, since they
 * would only get 402 answers. It asks again every 30 s, so the dashboard comes back by itself once
 * the licence is fixed.
 *
 * Recording is never affected: the lock is on the dashboard only.
 */
export default function LicenseGate({ children }: { children: ReactNode }) {
  const [status, setStatus] = useState<LicenseStatus | null>(null);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const s = await fetchLicenseStatus();
        if (active) setStatus(s);
      } catch {
        // An unknown state is not a lock: a failed status call must never hide a working dashboard.
      }
    }
    load();
    const id = setInterval(load, POLL_MS);
    return () => { active = false; clearInterval(id); };
  }, []);

  if (status?.locked) return <LockScreen reason={status.reason} />;

  return (
    <>
      {status?.reason === 'unreachable' && status.lockAfterUtc && (
        <Alert severity="warning" sx={{ mb: 2 }}>
          The licence server cannot be reached. The dashboard keeps working until{' '}
          {new Date(status.lockAfterUtc).toLocaleString()}, then it locks unless the server answers.
          Recording is not affected.
        </Alert>
      )}
      {children}
    </>
  );
}

function LockScreen({ reason }: { reason: LicenseStatus['reason'] }) {
  const why = reason === 'rejected'
    ? 'The licence for this gateway was not accepted.'
    : 'The licence server could not be reached for too long.';

  return (
    <Box sx={{ display: 'flex', justifyContent: 'center', alignItems: 'center', minHeight: '60vh', px: 2 }}>
      <Paper variant="outlined" sx={{ p: 4, maxWidth: 520, textAlign: 'center', borderRadius: 2 }}>
        <LockIcon sx={{ fontSize: 48, color: 'text.secondary', mb: 1 }} />
        <Typography variant="h5" sx={{ fontWeight: 700, mb: 1.5 }}>Dashboard locked</Typography>
        <Typography color="text.secondary" sx={{ mb: 2 }}>
          {why} Please contact Shot Sense support.
        </Typography>
        <Typography color="text.secondary" sx={{ mb: 2 }}>
          The machine data is still being recorded, so nothing is lost. The dashboard opens again by
          itself as soon as the licence is confirmed.
        </Typography>
        <Typography variant="caption" color="text.disabled">It checks again automatically.</Typography>
      </Paper>
    </Box>
  );
}
