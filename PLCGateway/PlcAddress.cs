public class PlcAddress
{
    public string Name { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Mode { get; set; } = string.Empty; // Read, Write, ReadWrite
    public string? DataType { get; set; } // INT, REAL, BOOL, STRING, DINT, etc.
    public object? WriteValue { get; set; } // Optional: default value to write for Write/ReadWrite tags
}
