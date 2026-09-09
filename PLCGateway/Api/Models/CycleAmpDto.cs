namespace PlcApi.Models;

/// <summary>One completed cycle's average current for a single impeller.</summary>
public class CycleAmpDto
{
    public int CycleNumber { get; set; }
    public DateTime BlastEnd { get; set; }
    public double? AvgAmps { get; set; }
}
