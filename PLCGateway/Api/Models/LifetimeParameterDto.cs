namespace PlcApi.Models;

public class LifetimeParameterDto
{
    public string ParameterName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; }
}
