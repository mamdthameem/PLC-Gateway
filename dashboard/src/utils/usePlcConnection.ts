import { useState, useEffect } from 'react';
import { fetchMachineStatus } from '../services/machineStatusService';

/**
 * Polls the gateway's PLC link state.
 *
 * Panels that show last-known tag values (live amps, spare health) use this to say so while the
 * PLC is unreachable — otherwise a frozen reading is indistinguishable from a live one. The
 * backend keeps serving the last values by design; only the labelling changes.
 */
export function usePlcConnection(pollMs = 10_000): { connected: boolean; lastScanAt: string | null } {
  const [connected, setConnected]   = useState(true);
  const [lastScanAt, setLastScanAt] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    async function load() {
      try {
        const s = await fetchMachineStatus();
        if (!active) return;
        setConnected(s.plcConnected);
        setLastScanAt(s.lastScanAt);
      } catch {
        // Leave the last known state alone — an API hiccup is not a PLC disconnect.
      }
    }
    load();
    const id = setInterval(load, pollMs);
    return () => { active = false; clearInterval(id); };
  }, [pollMs]);

  return { connected, lastScanAt };
}
