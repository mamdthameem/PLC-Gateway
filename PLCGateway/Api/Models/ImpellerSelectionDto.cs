namespace PlcApi.Models;

/// <summary>
/// The site's impeller selection (gateway_settings). GET returns every field; PUT reads only
/// <see cref="Selected"/>, which must be among <see cref="Available"/>.
/// </summary>
public class ImpellerSelectionDto
{
    public int[] Selected { get; set; } = [];

    /// <summary>The impellers this site's tag file has tags for — the only ones that can be selected.</summary>
    public int[] Available { get; set; } = [];

    public int MaxImpellers { get; set; }
}
