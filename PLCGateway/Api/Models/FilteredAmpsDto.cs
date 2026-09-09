namespace PlcApi.Models;

public class FilteredAmpsCyclePointDto
{
    public int CycleNumber { get; set; }
    public DateTime BlastEnd { get; set; }
    public double? AvgAmps { get; set; }
}

public class FilteredAmpsDto
{
    public int ImpellerNumber { get; set; }

    // Duration-weighted average across the cycles in scope (cycles with no in-window sample are
    // excluded from both the numerator and the denominator, not counted as zero).
    public double? OverallAvgAmps { get; set; }

    public List<FilteredAmpsCyclePointDto> Cycles { get; set; } = new();
}
