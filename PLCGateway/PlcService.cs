using S7.Net;
using System;

public class PlcService
{
    private readonly Plc _plc;

    public PlcService(string ip, short rack, short slot)
    {
        _plc = new Plc(CpuType.S71200, ip, rack, slot);
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
