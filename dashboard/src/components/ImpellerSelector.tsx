import { useEffect, useState } from 'react';
import {
  Alert, Box, Button, CircularProgress, Dialog, DialogActions, DialogContent,
  DialogContentText, DialogTitle, ToggleButton, ToggleButtonGroup, Typography,
} from '@mui/material';
import { fetchImpellerSelection, saveImpellerSelection } from '../services/settingsService';

/**
 * Picks which impellers the site includes. It sits in the Live Impeller Current header, but the
 * choice is machine-wide and saved on the gateway for every viewer: it decides which impellers
 * every panel shows AND which ones the calculations count (energy, the Section 2 current split,
 * spare monitoring).
 *
 * Saving recalculates the energy of every recorded cycle, which moves long-standing headline
 * figures, so it asks first. Nothing is deleted — selecting an impeller again brings its figures
 * straight back.
 */
export default function ImpellerSelector() {
  const [maxImpellers, setMaxImpellers] = useState(0);
  const [saved, setSaved]               = useState<number[]>([]);
  const [draft, setDraft]               = useState<number[]>([]);
  const [confirmOpen, setConfirmOpen]   = useState(false);
  const [saving, setSaving]             = useState(false);
  const [error, setError]               = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    fetchImpellerSelection()
      .then(s => {
        if (!active) return;
        setMaxImpellers(s.maxImpellers);
        setSaved(s.selected);
        setDraft(s.selected);
      })
      .catch(e => { if (active) setError((e as Error).message); });
    return () => { active = false; };
  }, []);

  const loaded  = maxImpellers > 0;
  const all     = Array.from({ length: maxImpellers }, (_, i) => i + 1);
  const ordered = [...draft].sort((a, b) => a - b);
  const dirty   = ordered.join(',') !== saved.join(',');

  async function save() {
    setSaving(true);
    setError(null);
    try {
      const s = await saveImpellerSelection(ordered);
      setSaved(s.selected);
      setDraft(s.selected);
      setConfirmOpen(false);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setSaving(false);
    }
  }

  return (
    <Box sx={{ display: 'flex', alignItems: 'center', flexWrap: 'wrap', gap: 1 }}>
      <Typography variant="caption" color="text.secondary" sx={{ fontWeight: 700 }}>
        Impellers
      </Typography>
      <ToggleButtonGroup
        size="small"
        value={draft}
        onChange={(_, value: number[]) => setDraft(value)}
        disabled={!loaded || saving}
        aria-label="Impellers included"
      >
        {all.map(n => (
          <ToggleButton key={n} value={n} sx={{ minWidth: 34, px: 1, py: 0.25, fontWeight: 700 }}>
            {n}
          </ToggleButton>
        ))}
      </ToggleButtonGroup>
      <Button size="small" onClick={() => setDraft(all)} disabled={!loaded || saving}>All</Button>
      <Button size="small" onClick={() => setDraft([])} disabled={!loaded || saving}>None</Button>
      <Button
        size="small"
        variant="contained"
        onClick={() => { setError(null); setConfirmOpen(true); }}
        disabled={!dirty || draft.length === 0 || saving}
      >
        Save
      </Button>
      {loaded && draft.length === 0 && (
        <Typography variant="caption" color="warning.main">Select at least one impeller</Typography>
      )}
      {error && !confirmOpen && (
        <Alert severity="error" sx={{ py: 0 }}>{error}</Alert>
      )}

      <Dialog
        open={confirmOpen}
        onClose={() => { if (!saving) setConfirmOpen(false); }}
        maxWidth="sm"
        fullWidth
      >
        <DialogTitle>Include impellers {ordered.join(', ')}?</DialogTitle>
        <DialogContent>
          <DialogContentText>
            Only these impellers will be shown and counted, for everyone using this gateway. The
            energy of every recorded cycle is recalculated from the stored current readings, so the
            energy totals, energy per casting and the energy graphs all change. Spare monitoring
            covers only these impellers.
          </DialogContentText>
          <DialogContentText sx={{ mt: 1.5 }}>
            No readings are deleted, and this can be changed back at any time. Filter results already
            on screen are not updated; apply the filter again to see them with this selection.
          </DialogContentText>
          {error && <Alert severity="error" sx={{ mt: 2 }}>{error}</Alert>}
        </DialogContent>
        <DialogActions>
          <Button onClick={() => setConfirmOpen(false)} disabled={saving}>Cancel</Button>
          <Button
            variant="contained"
            onClick={save}
            disabled={saving}
            startIcon={saving ? <CircularProgress size={14} color="inherit" /> : undefined}
          >
            {saving ? 'Recalculating…' : 'Save and recalculate'}
          </Button>
        </DialogActions>
      </Dialog>
    </Box>
  );
}
