using S7.Net;
using System;
using System.Net;
using System.Threading;

/// <summary>Where the PLC is: IP address, rack and slot.</summary>
public sealed record PlcEndpoint(string IpAddress, short Rack, short Slot)
{
    public override string ToString() => $"{IpAddress} rack {Rack} slot {Slot}";

    /// <summary>
    /// Null when acceptable, else the reason in one sentence. Only plant-network (private) IPv4
    /// addresses: the address can be set from the cloud, and the gateway must never be pointed at
    /// the internet. Rack 0–7, slot 0–31 (an S7-1200 is normally 0 and 1).
    /// </summary>
    public static string? Problem(string? ip, int rack, int slot)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip.Trim(), out var address) ||
            address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            ip.Trim().Split('.').Length != 4)
            return "Enter the PLC's IPv4 address, for example 192.168.1.11.";
        byte[] b = address.GetAddressBytes();
        bool privateAddress = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        if (!privateAddress)
            return "The PLC must be on the plant network: 10.x.x.x, 172.16–31.x.x or 192.168.x.x.";
        if (rack < 0 || rack > 7 || slot < 0 || slot > 31)
            return "Rack is 0 to 7 and slot 0 to 31. For an S7-1200 they are normally 0 and 1.";
        return null;
    }
}

public class PlcService
{
    private Plc _plc;
    private PlcEndpoint? _pending;

    public PlcService(string ip, short rack, short slot)
    {
        Endpoint = new PlcEndpoint(ip, rack, slot);
        _plc = Create(Endpoint);
    }

    /// <summary>The address the scan loop is using now.</summary>
    public PlcEndpoint Endpoint { get; private set; }

    private static Plc Create(PlcEndpoint e) => new(CpuType.S71200, e.IpAddress, e.Rack, e.Slot);

    /// <summary>
    /// Asks for a new PLC address. Thread-safe and immediate for the caller: the scan loop picks it up
    /// before its next read (<see cref="ApplyPendingEndpoint"/>), so a read in progress — or a
    /// connection attempt waiting on the old address — is never cut off from another thread.
    /// </summary>
    public void RequestEndpoint(PlcEndpoint endpoint) => Volatile.Write(ref _pending, endpoint);

    /// <summary>Scan loop only. Switches to a requested address; true when it changed.</summary>
    public bool ApplyPendingEndpoint()
    {
        var next = Interlocked.Exchange(ref _pending, null);
        if (next is null || next == Endpoint) return false;
        try { _plc.Close(); } catch { /* the old link may already be dead */ }
        _plc = Create(next);
        Endpoint = next;
        return true;
    }

    public bool IsConnected => _plc?.IsConnected ?? false;

    public void Connect()
    {
        if (_plc == null) throw new InvalidOperationException("PLC object not initialized.");
        if (!_plc.IsConnected)
        {
            _plc.Open();
        }
    }

    // Reads a contiguous byte region of a data block in one call. S7.NetPlus splits reads
    // larger than the negotiated PDU into multiple requests internally. The scan loop reads a
    // few large regions per pass instead of one request per tag, then parses tags via TagParser.
    public byte[] ReadRegion(int dbNumber, int startByte, int count)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        return _plc.ReadBytes(DataType.DataBlock, dbNumber, startByte, count);
    }

    // RESERVED — not used by the current scan loop. Kept for the spares REPLACED write-back
    // flow (writing acknowledgement bits back to the PLC). Do not delete.
    public void Write(string address, object value)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        _plc.Write(address, value);
    }
}
