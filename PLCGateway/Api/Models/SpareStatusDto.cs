namespace PlcApi.Models;

public class SpareStatusDto
{
    public int ImpellerNum { get; set; }
    public int SpareIndex { get; set; }
    public string SpareName { get; set; } = string.Empty;
    public double ThresholdHours { get; set; }
    public double CurrentRunHours { get; set; }
    public bool TriggerActive { get; set; }
    public DateTime? LastReplacedAt { get; set; }
    public DateTime LastUpdatedAt { get; set; }
}
