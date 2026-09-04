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
    /// True when the source explicitly reported no result for this assessment — IXL writes "--"
    /// when a diagnostic was not completed, and other exports use "N/A" or "none". Distinguishing
    /// these from a label the ruleset simply cannot map is what lets staff tell "the test was not
    /// taken" from "we could not read this value", which are different problems with different
    /// remedies. A null or whitespace value is deliberately NOT treated as a placeholder: an
    /// absent column is indistinguishable from one this parser failed to read, so it keeps the
    /// more cautious "unrecognised" classification.
    /// </summary>
    public static bool IsNoResultPlaceholder(string? rawLabel)
    {
        if (string.IsNullOrWhiteSpace(rawLabel)) return false;
        var canonical = Canonicalise(rawLabel);
        // Canonicalise strips punctuation, so "--", "-", "*" and "." reduce to nothing at all.
        if (canonical.Length == 0) return true;
        return canonical is "n a" or "na" or "none" or "null" or "no result"
            or "not tested" or "not attempted" or "no score";
    }

    /// <summary>
    /// Resolves a raw performance/proficiency label to its 0-3 value for the given source
    /// (UploadType). Tries the source-specific map, then the shared aliases. Returns false (no
    /// percentile fallback, per TR-003) when nothing matches.
    /// </summary>
    public static bool TryResolve(string source, string? rawLabel, TierRulesetConfigDocument ruleset, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(rawLabel)) return false;

        var canonical = Canonicalise(rawLabel);
        if (canonical.Length == 0) return false;

        if (ruleset.CategoryValues.TryGetValue(source, out var sourceMap)
            && TryResolveIn(canonical, sourceMap, out value)) return true;

        return TryResolveIn(canonical, ruleset.SharedCategoryValues, out value);
    }

    /// <summary>
    /// Exact label match first, then the most specific key that occurs in the label as a complete
    /// phrase.
    ///
    /// Both halves of that used to be looser and both misread real labels. The fallback was a raw
    /// substring test, so the two-letter shared keys "at" and "on" matched inside unrelated words
    /// — "Str<b>at</b>egic", "W<b>at</b>ch", "Interventi<b>on</b>", "M<b>on</b>itor" all resolved
    /// to 2 ("at/on grade level"), scoring a student who needs support as if they were on grade
    /// level. Requiring whole tokens makes that impossible. And the first matching key used to win
    /// on dictionary order rather than specificity, so a bare "at" could beat "at risk"; longest
    /// phrase wins now, which makes the outcome independent of insertion order.
    ///
    /// Whole-token matching only ever narrows what matched before, so any label that resolved by
    /// exact lookup — which is every label in the LGS data as audited — is unaffected.
    /// </summary>
    private static bool TryResolveIn(string canonical, Dictionary<string, int> map, out int value)
    {
        value = 0;
        if (map.Count == 0) return false;

        foreach (var (key, val) in map)
            if (Canonicalise(key).Equals(canonical, StringComparison.OrdinalIgnoreCase)) { value = val; return true; }

        var labelTokens = canonical.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var bestTokenCount = 0;
        var bestKeyLength = 0;
        foreach (var (key, val) in map)
        {
            var canonicalKey = Canonicalise(key);
            var keyTokens = canonicalKey.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (keyTokens.Length == 0 || !ContainsPhrase(labelTokens, keyTokens)) continue;
            if (keyTokens.Length > bestTokenCount
                || (keyTokens.Length == bestTokenCount && canonicalKey.Length > bestKeyLength))
            {
                value = val;
                bestTokenCount = keyTokens.Length;
                bestKeyLength = canonicalKey.Length;
            }
        }
        return bestTokenCount > 0;
    }

    /// <summary>Does <paramref name="keyTokens"/> appear as a consecutive run of whole tokens
    /// inside <paramref name="labelTokens"/>?</summary>
    private static bool ContainsPhrase(string[] labelTokens, string[] keyTokens)
    {
        if (keyTokens.Length > labelTokens.Length) return false;
        for (var i = 0; i + keyTokens.Length <= labelTokens.Length; i++)
        {
            var matched = true;
            for (var j = 0; j < keyTokens.Length; j++)
            {
                if (!labelTokens[i + j].Equals(keyTokens[j], StringComparison.OrdinalIgnoreCase))
                {
                    matched = false;
                    break;
                }
            }
            if (matched) return true;
        }
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
