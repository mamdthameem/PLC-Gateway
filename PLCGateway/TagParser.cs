using System;
using System.Globalization;
using System.Text;

/// <summary>
/// Parses PLC tag values directly out of a raw DB byte region (Siemens S7 big-endian layout).
/// Replaces the old per-tag S7 read + ValueConverter path: the scan reads whole DB regions in
/// a few requests, then parses every tag from the in-memory buffer.
///
/// Output strings intentionally match the previous ValueConverter formatting so that Tier 1 /
/// Tier 2 storage and all downstream calculations are unchanged by the switch to batch reads.
/// </summary>
public static class TagParser
{
    public const int DefaultStringMaxLen = 254; // S7 STRING default: 2-byte header + up to 254 chars

    // Parses "DB60.DBX1.1" -> (60, 1, 1); "DB60.DBB0" -> (60, 0, 0); "DB60.DBD1650" -> (60, 1650, 0).
    // Handles DBB (byte), DBX (bit), DBW (word), DBD (dword) prefixes.
    public static (int Db, int ByteOffset, int Bit) ParseAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
            throw new FormatException("Empty PLC address.");

        var parts = address.Split('.');
        if (parts.Length < 2 || !parts[0].StartsWith("DB", StringComparison.OrdinalIgnoreCase))
            throw new FormatException($"Cannot parse DB address: {address}");

        int db = int.Parse(parts[0].Substring(2), CultureInfo.InvariantCulture);

        string area = parts[1];                      // e.g. "DBX1", "DBB0", "DBD1650"
        if (area.Length < 4)
            throw new FormatException($"Cannot parse DB area: {address}");

        int byteOffset = int.Parse(area.Substring(3), CultureInfo.InvariantCulture);
        int bit = 0;
        bool isBit = area.Substring(0, 3).Equals("DBX", StringComparison.OrdinalIgnoreCase);
        if (isBit && parts.Length >= 3)
            bit = int.Parse(parts[2], CultureInfo.InvariantCulture);

        return (db, byteOffset, bit);
    }

    // Number of bytes a tag occupies, used to size the region to read.
    public static int TypeLengthBytes(string dataType, int stringMaxLen = DefaultStringMaxLen)
    {
        switch ((dataType ?? "").ToUpperInvariant().Trim())
        {
            case "BOOL": case "BOOLEAN":
            case "BYTE": case "SINT": case "USINT":
                return 1;
            case "INT": case "WORD":
                return 2;
            case "DINT": case "DWORD": case "REAL": case "UDINT":
                return 4;
            case "STRING": case "CHAR": case "VARCHAR":
                return stringMaxLen + 2;
            default:
                return 4; // safe upper bound for unknown numeric types
        }
    }

    /// <summary>
    /// Parses one tag from <paramref name="region"/>, which holds the DB bytes starting at
    /// absolute byte <paramref name="regionStart"/>. Returns the formatted string value.
    /// </summary>
    public static string Parse(
        byte[] region, int regionStart,
        int byteOffset, int bit, string dataType,
        int stringMaxLen = DefaultStringMaxLen)
    {
        int o = byteOffset - regionStart;
        string type = (dataType ?? "").ToUpperInvariant().Trim();

        switch (type)
        {
            case "BOOL": case "BOOLEAN":
                RequireBytes(region, o, 1, byteOffset, type);
                return ((region[o] >> bit) & 0x01) != 0 ? "1" : "0";

            case "BYTE": case "USINT":
                RequireBytes(region, o, 1, byteOffset, type);
                return region[o].ToString(CultureInfo.InvariantCulture);

            case "SINT":
                RequireBytes(region, o, 1, byteOffset, type);
                return ((sbyte)region[o]).ToString(CultureInfo.InvariantCulture);

            case "INT":
                RequireBytes(region, o, 2, byteOffset, type);
                return ((short)((region[o] << 8) | region[o + 1])).ToString(CultureInfo.InvariantCulture);

            case "WORD":
                RequireBytes(region, o, 2, byteOffset, type);
                return ((ushort)((region[o] << 8) | region[o + 1])).ToString(CultureInfo.InvariantCulture);

            case "DINT":
                RequireBytes(region, o, 4, byteOffset, type);
                return ReadInt32BE(region, o).ToString(CultureInfo.InvariantCulture);

            case "DWORD": case "UDINT":
                RequireBytes(region, o, 4, byteOffset, type);
                return ((uint)ReadInt32BE(region, o)).ToString(CultureInfo.InvariantCulture);

            case "REAL":
                RequireBytes(region, o, 4, byteOffset, type);
                return BitConverter.Int32BitsToSingle(ReadInt32BE(region, o))
                    .ToString(CultureInfo.InvariantCulture);

            case "STRING": case "CHAR": case "VARCHAR":
                return ParseS7String(region, o, byteOffset, stringMaxLen);

            default:
                // Unknown type: treat as DINT to preserve a numeric-ish value rather than throwing.
                RequireBytes(region, o, 4, byteOffset, type);
                return ReadInt32BE(region, o).ToString(CultureInfo.InvariantCulture);
        }
    }

    private static int ReadInt32BE(byte[] b, int o) =>
        (b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3];

    // S7 STRING: byte[o] = declared max length, byte[o+1] = current length, then ASCII characters.
    private static string ParseS7String(byte[] region, int o, int absOffset, int stringMaxLen)
    {
        RequireBytes(region, o, 2, absOffset, "STRING");
        int curLen = Math.Min(region[o + 1], stringMaxLen);
        if (curLen <= 0) return "";

        int available = region.Length - (o + 2);
        if (available < curLen) curLen = Math.Max(available, 0);
        if (curLen <= 0) return "";

        return Encoding.ASCII.GetString(region, o + 2, curLen).Trim('\0');
    }

    private static void RequireBytes(byte[] region, int o, int need, int absOffset, string type)
    {
        if (o < 0 || o + need > region.Length)
            throw new IndexOutOfRangeException(
                $"Tag at byte {absOffset} ({type}) needs {need} byte(s) at region index {o}, " +
                $"but region length is {region.Length}.");
    }
}
