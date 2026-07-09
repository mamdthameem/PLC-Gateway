namespace PlcApi.Models;

public class FilterRequestInput
{
    public DateTime FilterStart { get; set; }
    public DateTime FilterEnd { get; set; }
    public string? PeriodLabel { get; set; }
    public string FilterBy { get; set; } = "time";         // "time" | "cycle" | "metal"
    public int? FilterCycleFrom { get; set; }
    public int? FilterCycleTo { get; set; }
    public string? FilterMetalName { get; set; }
}

public class FilterStatusDto
{
    public string Status { get; set; } = string.Empty;
    public DateTime? ProcessedAt { get; set; }
}
