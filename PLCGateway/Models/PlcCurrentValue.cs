namespace PLCGateway.Models;

public class PlcCurrentValue
{
    public string Address { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string DataType { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
    public DateTime? LastStoredHistorical { get; set; }
    public DateTime? LastHeartbeat { get; set; }
    public bool IsStale { get; set; }
}
