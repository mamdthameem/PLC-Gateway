namespace PlcApi.Models;

public class MachineStatusDto
{
    public string Value       { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }

    // PLC link state (Part A5): when PlcConnected is false the dashboard shows a
    // "PLC disconnected" banner and treats live tiles as stale.
    public bool PlcConnected { get; set; }
    public bool IsStale      { get; set; }
    public DateTime? LastScanAt { get; set; }
}
