using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

/// <summary>
/// The impellers the site has chosen to include. Machine-wide, not per viewer: it decides which
/// impellers every dashboard panel shows AND which ones the calculations count — per-cycle energy,
/// the Section 2 current split and spare monitoring. Raw Current_imp_N readings keep being
/// recorded for every impeller regardless, so selecting one again later loses nothing.
///
/// Two lists (2026-10-26):
///   available — the impellers this site's TAG FILE has tags for (a current, spare-runhour, spare-trigger
///               or REPLACED tag numbered N). A machine with one impeller has tags for impeller 1 only,
///               so impellers 2–10 never appear anywhere.
///   saved     — the choice stored in gateway_settings and changed from the cloud. Defaults to all ten.
/// Every reader gets <see cref="Snapshot"/>: the saved choice limited to the available impellers, or all
/// the available ones when that leaves nothing. A choice is saved only from available impellers.
///
/// Loaded at startup and replaced by CalculationService.ApplyImpellerSelectionAsync on a save.
/// </summary>
public class ImpellerSelection
{
    /// <summary>The most impellers a machine can have; DB60 carries tags for impellers 1–10.</summary>
    public const int MaxImpellers = 10;

    // Tag names that mean "this site has impeller N".
    private static readonly Regex[] ImpellerTagPatterns =
    {
        new(@"^Current_imp_(\d+)$"),
        new(@"^Spares_Runhour_imp(\d+)\["),
        new(@"^Spares trigger imp(\d+)\["),
        new(@"^REPLACED_(\d+)\["),
    };

    private readonly int[] _available;
    private volatile int[] _saved = Enumerable.Range(1, MaxImpellers).ToArray();

    public ImpellerSelection(IConfiguration config) : this(AvailableFromTags(config)) { }

    public ImpellerSelection(IEnumerable<int> available)
    {
        _available = available.Where(i => i >= 1 && i <= MaxImpellers).Distinct().OrderBy(i => i).ToArray();
    }

    /// <summary>The impellers the tag file has tags for, ascending. Empty for a site with none.</summary>
    public int[] Available() => (int[])_available.Clone();

    /// <summary>
    /// The impellers in use, ascending — safe to hand to a query as a parameter. The saved choice
    /// limited to the available impellers; all available ones when that leaves nothing.
    /// </summary>
    public int[] Snapshot()
    {
        var inUse = _saved.Intersect(_available).ToArray();
        return inUse.Length > 0 ? inUse : Available();
    }

    /// <summary>The live current tag name of each impeller in use.</summary>
    public string[] CurrentTagNames() => Snapshot().Select(i => $"Current_imp_{i}").ToArray();

    /// <summary>Replaces the saved choice (startup load, or a save from the cloud).</summary>
    public void Set(IEnumerable<int> impellers) => _saved = Normalize(impellers);

    /// <summary>The saved choice as stored, before it is limited to the available impellers.</summary>
    public int[] Saved() => (int[])_saved.Clone();

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

    /// <summary>Impeller numbers named by the configured tags (any mode but write-only).</summary>
    public static int[] AvailableFromTags(IConfiguration config)
    {
        var found = new SortedSet<int>();
        foreach (var tag in config.GetSection("Tags").GetChildren())
        {
            if (string.Equals(tag["Mode"], "write", StringComparison.OrdinalIgnoreCase)) continue;
            string name = tag["Name"] ?? "";
            foreach (var pattern in ImpellerTagPatterns)
            {
                var m = pattern.Match(name);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n >= 1 && n <= MaxImpellers)
                    found.Add(n);
            }
        }
        return found.ToArray();
    }
}
