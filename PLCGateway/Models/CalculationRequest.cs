using System;

namespace PLCGateway.Models
{
    public class CalculationRequest
    {
        public int Id { get; set; }
        public DateTime FilterStart { get; set; }
        public DateTime FilterEnd { get; set; }
        public string? PeriodLabel { get; set; }

        // Filter mode: 'time' | 'cycle' | 'metal'
        // ('metal' is the stored wire value; the dashboard displays it as "Item" — see README.)
        public string FilterBy { get; set; } = "time";
        public int? FilterCycleFrom { get; set; }
        public int? FilterCycleTo { get; set; }
        public string? FilterMetalName { get; set; }

        // Section 2 parameter keys to compute (CalculationService.Section2ParameterKeys).
        // Null or empty means "all of them" — what every row written before the per-parameter
        // toggles existed means, and what a caller that omits the field means.
        public string[]? SelectedParameters { get; set; }

        public string Status { get; set; } = "pending";
        public DateTime CreatedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}
