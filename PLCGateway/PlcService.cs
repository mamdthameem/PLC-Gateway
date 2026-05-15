using S7.Net;
using System;
using System.Text;

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

    public object Read(string address)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        return _plc.Read(address);
    }

    // Reads a Siemens S7 STRING from a DB address (e.g. "DB60.DBX6.0").
    // S7 STRING layout: byte[0]=maxLen, byte[1]=currentLen, byte[2..]=ASCII chars.
    // Parses the DB number and byte offset from the address and reads raw bytes.
    public string ReadString(string address, int maxLength = 254)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");

        var (dbNumber, byteOffset) = ParseDbByteOffset(address);

        // Read maxLength + 2 bytes (2-byte header + character data)
        byte[] bytes = _plc.ReadBytes(DataType.DataBlock, dbNumber, byteOffset, maxLength + 2);
        if (bytes == null || bytes.Length < 2) return "";

        int currentLen = Math.Min(bytes[1], maxLength);
        if (currentLen <= 0) return "";

        return Encoding.ASCII.GetString(bytes, 2, currentLen);
    }

    public void Write(string address, object value)
    {
        if (!_plc.IsConnected) throw new InvalidOperationException("PLC not connected");
        _plc.Write(address, value);
    }

    // Parses "DB60.DBX6.0" → (dbNumber=60, byteOffset=6)
    // Works for any DBX/DBB/DBW/DBD prefix.
    private static (int dbNumber, int byteOffset) ParseDbByteOffset(string address)
    {
        var parts = address.Split('.');
        if (parts.Length < 2)
            throw new FormatException($"Cannot parse DB address: {address}");

        // parts[0] = "DB60"
        int dbNumber = int.Parse(parts[0].Substring(2));

        // parts[1] = "DBX6" or "DBB6" or "DBD6" — strip the 3-char prefix
        string offsetPart = parts[1].Length > 3 ? parts[1].Substring(3) : parts[1];
        int byteOffset = int.Parse(offsetPart);

        return (dbNumber, byteOffset);
    }
}
