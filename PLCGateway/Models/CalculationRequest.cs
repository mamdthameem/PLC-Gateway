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
        public string FilterBy { get; set; } = "time";
        public int? FilterCycleFrom { get; set; }
        public int? FilterCycleTo { get; set; }
        public string? FilterMetalName { get; set; }

        public string Status { get; set; } = "pending";
        public DateTime CreatedAt { get; set; }
        public DateTime? ProcessedAt { get; set; }
    }
}
