namespace PLCGateway.Models;

// Buffered Tier 1 upsert for one tag in a scan pass (written in bulk by WriteScanBatchAsync).
public sealed class Tier1Write
{
    public string Address { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string DataType { get; set; } = string.Empty;
}

// Buffered Tier 2 (historical) insert for one tag in a scan pass.
public sealed class Tier2Write
{
    public string Address { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string StorageReason { get; set; } = string.Empty;
    public string? PreviousValue { get; set; }
}
