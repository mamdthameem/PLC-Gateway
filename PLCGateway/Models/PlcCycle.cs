using System;

namespace PLCGateway.Models
{
    public class PlcCycle
    {
        public int CycleNumber { get; set; }
        public DateTime BlastStart { get; set; }
        public DateTime BlastEnd { get; set; }
        public double DurationSec { get; set; }
        public string? Metal1Name { get; set; }
        public double? Metal1WeightKg { get; set; }
        public string? Metal2Name { get; set; }
        public double? Metal2WeightKg { get; set; }
        public string? Metal3Name { get; set; }
        public double? Metal3WeightKg { get; set; }
        public string? Metal4Name { get; set; }
        public double? Metal4WeightKg { get; set; }
        public double? TonnageKg { get; set; }
    }
}
