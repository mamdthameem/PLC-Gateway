namespace PlcApi.Models;

/// <summary>
/// The site's impeller selection (gateway_settings). GET returns both fields; PUT reads only
/// <see cref="Selected"/>.
/// </summary>
public class ImpellerSelectionDto
{
    public int[] Selected { get; set; } = [];
    public int MaxImpellers { get; set; }
}
