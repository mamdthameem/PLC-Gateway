namespace PLCGateway.Models;

public class PlcAggregatedHourly
{
    public int Id { get; set; }
    public string Address { get; set; } = string.Empty;
    public string ParameterName { get; set; } = string.Empty;
    public DateTime HourTimestamp { get; set; }
    public decimal? MinValue { get; set; }
    public decimal? MaxValue { get; set; }
    public decimal? AvgValue { get; set; }
    public int? SampleCount { get; set; }
    public DateTime AggregationDate { get; set; }
    public int DataAgeDays { get; set; }
}
