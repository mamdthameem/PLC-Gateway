using System;
using System.Globalization;

/// <summary>
/// Converts raw PLC values from S7.Net library to proper string representations for database storage.
/// Handles all Siemens S7-1200 data types correctly.
/// </summary>
public static class ValueConverter
{
    /// <summary>
    /// Converts a raw PLC value to a string representation based on the data type.
    /// </summary>
    /// <param name="rawValue">The raw value returned from S7.Net Read()</param>
    /// <param name="dataType">The data type as specified in configuration (REAL, BOOL, INT, etc.)</param>
    /// <returns>String representation of the value, or empty string if conversion fails</returns>
    public static string ConvertToString(object? rawValue, string dataType)
    {
        if (rawValue == null)
            return "";

        if (rawValue is DBNull)
            return "";

        try
        {
            string typeUpper = (dataType ?? "").ToUpper().Trim();

            return typeUpper switch
            {
                "REAL" => ConvertReal(rawValue),
                "BOOL" or "BOOLEAN" => ConvertBool(rawValue),
                "INT" => ConvertInt(rawValue),
                "DINT" => ConvertDInt(rawValue),
                "WORD" => ConvertWord(rawValue),
                "DWORD" => ConvertDWord(rawValue),
                "BYTE" => ConvertByte(rawValue),
                "STRING" or "CHAR" or "VARCHAR" => ConvertString(rawValue),
                "SINT" => ConvertSInt(rawValue),
                "USINT" => ConvertUSInt(rawValue),
                "UDINT" => ConvertUDInt(rawValue),
                _ => ConvertDefault(rawValue)
            };
        }
        catch (Exception)
        {
            // If conversion fails, try default conversion
            return ConvertDefault(rawValue);
        }
    }

    /// <summary>
    /// Converts REAL (32-bit IEEE 754 float) value.
    /// S7.Net returns REAL as uint that needs proper conversion.
    /// </summary>
    private static string ConvertReal(object rawValue)
    {
        try
        {
            // S7.Net returns REAL as uint (4 bytes in IEEE 754 format)
            if (rawValue is uint uintValue)
            {
                // Convert uint to float using BitConverter
                byte[] bytes = BitConverter.GetBytes(uintValue);
                float floatValue = BitConverter.ToSingle(bytes, 0);
                return floatValue.ToString(CultureInfo.InvariantCulture);
            }
            
            // If it's already a float/double, use it directly
            if (rawValue is float f)
                return f.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is double d)
                return d.ToString(CultureInfo.InvariantCulture);
            
            // Try to parse as number and convert
            if (rawValue is IConvertible convertible)
            {
                float floatVal = convertible.ToSingle(CultureInfo.InvariantCulture);
                return floatVal.ToString(CultureInfo.InvariantCulture);
            }
            
            // Last resort: try parsing the string representation
            if (float.TryParse(rawValue.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                return parsed.ToString(CultureInfo.InvariantCulture);
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts BOOL value (single bit).
    /// </summary>
    private static string ConvertBool(object rawValue)
    {
        try
        {
            // S7.Net might return bool, byte, int, or uint
            if (rawValue is bool boolValue)
                return boolValue ? "1" : "0";
            
            if (rawValue is byte byteValue)
                return (byteValue != 0) ? "1" : "0";
            
            if (rawValue is int intValue)
                return (intValue != 0) ? "1" : "0";
            
            if (rawValue is uint uintValue)
                return (uintValue != 0) ? "1" : "0";
            
            // Try to convert to boolean
            if (rawValue is IConvertible convertible)
            {
                bool boolVal = convertible.ToBoolean(CultureInfo.InvariantCulture);
                return boolVal ? "1" : "0";
            }
            
            // Try parsing as number
            string str = rawValue.ToString() ?? "";
            if (int.TryParse(str, out int parsed))
                return (parsed != 0) ? "1" : "0";
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts INT (16-bit signed integer).
    /// </summary>
    private static string ConvertInt(object rawValue)
    {
        try
        {
            if (rawValue is short shortValue)
                return shortValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is int intValue)
                return intValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                short shortVal = convertible.ToInt16(CultureInfo.InvariantCulture);
                return shortVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts DINT (32-bit signed integer).
    /// </summary>
    private static string ConvertDInt(object rawValue)
    {
        try
        {
            if (rawValue is int intValue)
                return intValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is long longValue)
                return longValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                int intVal = convertible.ToInt32(CultureInfo.InvariantCulture);
                return intVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts WORD (16-bit unsigned integer).
    /// </summary>
    private static string ConvertWord(object rawValue)
    {
        try
        {
            if (rawValue is ushort ushortValue)
                return ushortValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is uint uintValue)
                return uintValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                ushort ushortVal = convertible.ToUInt16(CultureInfo.InvariantCulture);
                return ushortVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts DWORD (32-bit unsigned integer).
    /// </summary>
    private static string ConvertDWord(object rawValue)
    {
        try
        {
            if (rawValue is uint uintValue)
                return uintValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is ulong ulongValue)
                return ulongValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                uint uintVal = convertible.ToUInt32(CultureInfo.InvariantCulture);
                return uintVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts BYTE (8-bit unsigned integer).
    /// </summary>
    private static string ConvertByte(object rawValue)
    {
        try
        {
            if (rawValue is byte byteValue)
                return byteValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                byte byteVal = convertible.ToByte(CultureInfo.InvariantCulture);
                return byteVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts SINT (8-bit signed integer).
    /// </summary>
    private static string ConvertSInt(object rawValue)
    {
        try
        {
            if (rawValue is sbyte sbyteValue)
                return sbyteValue.ToString(CultureInfo.InvariantCulture);
            
            if (rawValue is IConvertible convertible)
            {
                sbyte sbyteVal = convertible.ToSByte(CultureInfo.InvariantCulture);
                return sbyteVal.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch
        {
            // Fall through to default
        }
        
        return ConvertDefault(rawValue);
    }

    /// <summary>
    /// Converts USINT (8-bit unsigned integer).
    /// </summary>
    private static string ConvertUSInt(object rawValue)
    {
        return ConvertByte(rawValue); // Same as BYTE
    }

    /// <summary>
    /// Converts UDINT (32-bit unsigned integer).
    /// </summary>
    private static string ConvertUDInt(object rawValue)
    {
        return ConvertDWord(rawValue); // Same as DWORD
    }

    /// <summary>
    /// Converts STRING value.
    /// </summary>
    private static string ConvertString(object rawValue)
    {
        if (rawValue is string str)
            return str;
        
        return rawValue?.ToString() ?? "";
    }

    /// <summary>
    /// Default conversion - uses ToString() as fallback.
    /// </summary>
    private static string ConvertDefault(object rawValue)
    {
        return rawValue?.ToString() ?? "";
    }
}
