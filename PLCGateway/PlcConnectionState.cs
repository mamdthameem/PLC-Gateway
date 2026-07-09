using System;

/// <summary>
/// Shared PLC connection state. GatewayWorker drives it from scan-pass results;
/// other services read it (SpareMonitoringService skips its poll while disconnected,
/// so stale Tier 1 values are never treated as live).
/// Starts disconnected; the first successful scan pass marks it connected.
/// </summary>
public class PlcConnectionState
{
    private readonly object _lock = new();
    private int _consecutiveFailures;

    public bool IsConnected { get; private set; }
    public DateTime? LastSuccessfulScanAt { get; private set; }

    /// <summary>Returns true when this success transitions the state from disconnected to connected.</summary>
    public bool RecordScanSuccess(DateTime scanAt)
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            LastSuccessfulScanAt = scanAt;
            if (IsConnected) return false;
            IsConnected = true;
            return true;
        }
    }

    /// <summary>
    /// Returns true when this failure transitions the state from connected to disconnected
    /// (after <paramref name="disconnectAfterFailures"/> consecutive failed scan passes).
    /// </summary>
    public bool RecordScanFailure(int disconnectAfterFailures)
    {
        lock (_lock)
        {
            _consecutiveFailures++;
            if (!IsConnected) return false;
            if (_consecutiveFailures < disconnectAfterFailures) return false;
            IsConnected = false;
            return true;
        }
    }
}
