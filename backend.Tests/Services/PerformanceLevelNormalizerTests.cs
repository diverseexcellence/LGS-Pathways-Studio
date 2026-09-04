using LgsImpact.Api.Services;
using LgsImpact.Api.Tests.TestData;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// Label -> 0-3 resolution. The bug these exist to prevent: the fallback used a raw substring
/// test against the shared aliases, whose keys include the two-letter fragments "at" and "on".
/// Those matched inside unrelated words, so a support level read as grade-level performance —
/// "Strategic", "Watch", "Urgent Intervention" and "Monitor" all resolved to 2. Every mistake ran
/// in the same unsafe direction: a student needing intervention scored as on grade level, which is
/// the exact failure an MTSS tiering system exists to prevent.
///
/// Whole-token matching also has to be conservative in the other direction: an unmappable label
/// must stay unresolved so it is excluded and reported, never guessed at.
/// </summary>
public class PerformanceLevelNormalizerTests
{
    private static readonly LgsImpact.Api.Models.TierRulesetConfigDocument Ruleset = RulesetFixture.Default();

    private static int? Resolve(string source, string? label) =>
        PerformanceLevelNormalizer.TryResolve(source, label, Ruleset, out var v) ? v : null;

    // ── the mis-resolutions found in the QA label matrix ────────────────────────

    [Theory]
    [InlineData("Strategic")]            // contains "at" inside strATegic
    [InlineData("Watch")]                // contains "at" inside wATch
    [InlineData("Urgent Intervention")]  // contains "on" inside interventiON
    [InlineData("Monitor")]              // contains "on" inside mONitor
    [InlineData("Intensive")]
    [InlineData("Support")]
    [InlineData("Core")]
    [InlineData("Not Proficient")]
    public void LabelsWithNoWholeTokenMatch_StayUnresolved(string label)
    {
        // Unresolved is the safe outcome: the record is excluded and reported, rather than being
        // assigned a value nobody intended.
        Assert.Null(Resolve("Acadience", label));
    }

    [Theory]
    [InlineData("At Risk", 0)]      // "at risk" beats the bare "at" on specificity
    [InlineData("Not At Risk", 2)]  // "not at risk" beats both
    public void ScreenerWordingWhereAtInvertsTheMeaning_ResolvesToTheSupportNeed(string label, int expected)
    {
        Assert.Equal(expected, Resolve("Acadience", label));
    }

    // ── everything that already worked must keep working ────────────────────────

    [Theory]
    [InlineData("Acadience", "Well Below Benchmark", 0)]
    [InlineData("Acadience", "Below Benchmark", 1)]
    [InlineData("Acadience", "At Benchmark", 2)]
    [InlineData("Acadience", "Above Benchmark", 3)]
    [InlineData("Acadience", "At or Above Benchmark", 3)]
    [InlineData("ILEARN", "Below Proficiency", 0)]
    [InlineData("ILEARN", "Approaching Proficiency", 1)]
    [InlineData("ILEARN", "At Proficiency", 2)]
    [InlineData("ILEARN", "Above Proficiency", 3)]
    [InlineData("IXL", "Far below grade level", 0)]
    [InlineData("IXL", "Below grade level", 1)]
    [InlineData("IXL", "On grade level", 2)]
    [InlineData("IXL", "Above grade level", 3)]
    [InlineData("IXL", "On Track", 2)]
    [InlineData("ILEARN", "Below Basic", 0)]
    [InlineData("ILEARN", "Approaching Standard", 1)]
    [InlineData("ILEARN", "Meets Standard", 2)]
    [InlineData("ILEARN", "Exceeds Standard", 3)]
    public void KnownVocabulary_ResolvesUnchanged(string source, string label, int expected)
    {
        Assert.Equal(expected, Resolve(source, label));
    }

    [Theory]
    [InlineData("at proficiency")]
    [InlineData("AT PROFICIENCY")]
    [InlineData("At Proficiency (Level 3)")]
    public void CaseAndTrailingDetail_DoNotChangeResolution(string label)
    {
        Assert.Equal(2, Resolve("ILEARN", label));
    }

    [Fact]
    public void MostSpecificPhraseWins_RegardlessOfMapOrder()
    {
        // "far below grade level" must not lose to the shorter "below grade level".
        Assert.Equal(0, Resolve("IXL", "Far below grade level"));
        Assert.Equal(0, Resolve("Acadience", "Well Below Benchmark"));
    }

    // ── no-result placeholders ──────────────────────────────────────────────────

    [Theory]
    [InlineData("--")]
    [InlineData("-")]
    [InlineData("N/A")]
    [InlineData("NA")]
    [InlineData("none")]
    [InlineData("Not Tested")]
    public void ExplicitNoResultValues_AreRecognisedAsPlaceholders(string label)
    {
        Assert.True(PerformanceLevelNormalizer.IsNoResultPlaceholder(label));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentValues_AreNotTreatedAsPlaceholders(string? label)
    {
        // An absent column cannot be distinguished from one the parser failed to read, so it keeps
        // the more cautious "unrecognised" classification rather than claiming the test was skipped.
        Assert.False(PerformanceLevelNormalizer.IsNoResultPlaceholder(label));
    }

    [Theory]
    [InlineData("At Benchmark")]
    [InlineData("Below Proficiency")]
    [InlineData("Watch")]
    public void RealLabels_AreNotPlaceholders(string label)
    {
        Assert.False(PerformanceLevelNormalizer.IsNoResultPlaceholder(label));
    }

    // ── band helper stays consistent with the resolver ──────────────────────────

    [Theory]
    [InlineData("Below Proficiency", "below")]
    [InlineData("Approaching Proficiency", "approaching")]
    [InlineData("At Proficiency", "on")]
    [InlineData("Above Proficiency", "above")]
    public void BandLabels_TrackTheResolvedValue(string label, string expectedBand)
    {
        Assert.Equal(expectedBand, PerformanceLevelNormalizer.TryResolveBand("ILEARN", label, Ruleset));
    }

    [Fact]
    public void BandIsNullWhenTheLabelCannotBeResolved()
    {
        Assert.Null(PerformanceLevelNormalizer.TryResolveBand("Acadience", "Watch", Ruleset));
    }
}
