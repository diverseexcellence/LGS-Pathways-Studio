using System.Text.RegularExpressions;
using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

/// <summary>
/// Single, config-driven category -&gt; 0-3 performance value resolver (TR-003). Replaces the five
/// divergent keyword-matching implementations that previously existed across
/// TierCalculationService, DashboardController (x2), SchoolAverageService, and StudentProfile.tsx.
/// </summary>
public static class PerformanceLevelNormalizer
{
    /// <summary>Lowercases, strips punctuation, and collapses whitespace so admin-entered config
    /// values and messy source labels ("At Proficiency (Level 3)") compare consistently.</summary>
    public static string Canonicalise(string label)
    {
        var lower = label.Trim().ToLowerInvariant();
        var noPunct = Regex.Replace(lower, @"[^\w\s]", " ");
        return Regex.Replace(noPunct, @"\s+", " ").Trim();
    }

    /// <summary>
    /// Resolves a raw performance/proficiency label to its 0-3 value for the given source
    /// (UploadType). Tries the source-specific map, then the shared aliases, then a contains-based
    /// pass over both so suffixed/prefixed labels still resolve. Returns false (no percentile
    /// fallback, per TR-003) when nothing matches.
    /// </summary>
    public static bool TryResolve(string source, string? rawLabel, TierRulesetConfigDocument ruleset, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(rawLabel)) return false;

        var canonical = Canonicalise(rawLabel);
        if (canonical.Length == 0) return false;

        if (ruleset.CategoryValues.TryGetValue(source, out var sourceMap))
        {
            if (sourceMap.TryGetValue(canonical, out value)) return true;
            foreach (var (key, val) in sourceMap)
                if (canonical.Contains(key, StringComparison.OrdinalIgnoreCase)) { value = val; return true; }
        }

        if (ruleset.SharedCategoryValues.TryGetValue(canonical, out value)) return true;
        foreach (var (key, val) in ruleset.SharedCategoryValues)
            if (canonical.Contains(key, StringComparison.OrdinalIgnoreCase)) { value = val; return true; }

        return false;
    }

    /// <summary>Coarse four-band label (for dashboard display only — not used for tiering) derived
    /// from the same 0-3 resolution, so the dashboard can never disagree with the tier engine about
    /// what a given label means.</summary>
    public static string? TryResolveBand(string source, string? rawLabel, TierRulesetConfigDocument ruleset)
    {
        if (!TryResolve(source, rawLabel, ruleset, out var value)) return null;
        return value switch
        {
            0 => "below",
            1 => "approaching",
            2 => "on",
            3 => "above",
            _ => null,
        };
    }
}
