namespace PlcApi.Models;

public class AmpReadingDto
{
    public string ParameterName { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTime LastUpdated { get; set; }
}
