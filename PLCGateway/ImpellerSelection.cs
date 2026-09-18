using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// The impellers the site has chosen to include. Machine-wide, not per viewer: it decides which
/// impellers every dashboard panel shows AND which ones the calculations count — per-cycle energy,
/// the Section 2 current split and spare monitoring. Raw Current_imp_N readings keep being
/// recorded for every impeller regardless, so selecting one again later loses nothing.
///
/// Persisted in gateway_settings; this singleton is the in-memory copy every service reads. It is
/// loaded at startup and replaced by CalculationService.ApplyImpellerSelectionAsync on a save.
/// Until then it holds every impeller.
/// </summary>
public class ImpellerSelection
{
    /// <summary>DB60 carries current, spare and REPLACED tags for impellers 1–10.</summary>
    public const int MaxImpellers = 10;

    private volatile int[] _selected = Enumerable.Range(1, MaxImpellers).ToArray();

    /// <summary>A copy of the selection, ascending — safe to hand to a query as a parameter.</summary>
    public int[] Snapshot() => (int[])_selected.Clone();

    /// <summary>The live current tag name of each selected impeller.</summary>
    public string[] CurrentTagNames() => _selected.Select(i => $"Current_imp_{i}").ToArray();

    public void Set(IEnumerable<int> impellers) => _selected = Normalize(impellers);

    /// <summary>Ascending and distinct. Throws on an empty list or an impeller outside 1–MaxImpellers.</summary>
    public static int[] Normalize(IEnumerable<int> impellers)
    {
        var result = impellers.Distinct().OrderBy(i => i).ToArray();
        if (result.Length == 0)
            throw new ArgumentException("At least one impeller must be selected.");
        if (result[0] < 1 || result[^1] > MaxImpellers)
            throw new ArgumentException($"Impellers must be between 1 and {MaxImpellers}.");
        return result;
    }
}
