namespace PlcApi.Models;

// One saved Section 2 calculation request (one calculation_requests row): what was asked for and
// where it stands. Looked up BY ID only - by the dashboard reopening a result from its URL, and by
// GET /api/admin/filter/{id}. There is deliberately no "latest" lookup any more: one viewer must
// never be handed the filter another viewer applied (see CONTRACT-admin-api.md, 2026-10-12).
public class FilteredRequestInfoDto
{
    public int RequestId { get; set; }
    public string Status { get; set; } = "pending";   // pending | processing | done | error
    public string FilterBy { get; set; } = "time";
    public DateTime FilterStart { get; set; }
    public DateTime FilterEnd { get; set; }
    public string? PeriodLabel { get; set; }
    public int? FilterCycleFrom { get; set; }
    public int? FilterCycleTo { get; set; }
    public string? FilterMetalName { get; set; }
    public DateTime? ProcessedAt { get; set; }
    // calculation_requests.selected_parameters — null means every parameter was computed.
    public string[]? SelectedParameters { get; set; }
}
