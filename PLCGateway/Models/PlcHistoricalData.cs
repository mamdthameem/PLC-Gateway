namespace PLCGateway.Models;

public class PlcHistoricalData
{
    public int Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string DataType { get; set; } = string.Empty;
    public string StorageReason { get; set; } = string.Empty; // COV, PERIODIC, STATE_CHANGE, VALUE_CHANGE
    public DateTime Timestamp { get; set; }
    public string? PreviousValue { get; set; }
}
