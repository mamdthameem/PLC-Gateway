using System.Text.Json;

namespace PLCGateway.Models;

public class CalculatedMetric
{
    public int Id { get; set; }
    public string MetricName { get; set; } = string.Empty;
    public decimal? MetricValue { get; set; }
    public string PeriodType { get; set; } = string.Empty; // realtime, hour, shift, day, week, month, year, cumulative
    public DateTime? PeriodValue { get; set; }
    public DateTime CalculationTimestamp { get; set; }
    public JsonDocument? Metadata { get; set; }
}
