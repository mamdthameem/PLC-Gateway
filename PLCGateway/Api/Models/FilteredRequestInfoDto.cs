namespace PlcApi.Models;

// Metadata of a completed Section 2 calculation request (one calculation_requests row).
// Used by the admin live snapshot to mirror the latest filtered view without recomputing.
public class FilteredRequestInfoDto
{
    public int RequestId { get; set; }
    public string FilterBy { get; set; } = "time";
    public DateTime FilterStart { get; set; }
    public DateTime FilterEnd { get; set; }
    public string? PeriodLabel { get; set; }
    public int? FilterCycleFrom { get; set; }
    public int? FilterCycleTo { get; set; }
    public string? FilterMetalName { get; set; }
    public DateTime? ProcessedAt { get; set; }
}
