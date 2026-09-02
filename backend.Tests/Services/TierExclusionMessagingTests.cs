using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using LgsImpact.Api.Tests.TestData;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// The reasoning string is what staff read to understand a tier, so an excluded assessment has to
/// say plainly that it was not counted and why. It used to print raw engine tokens
/// ("(unknown_period)"), which does not distinguish an expected omission (a superseded duplicate)
/// from a fixable data problem (a period that could not be identified).
/// </summary>
public class TierExclusionMessagingTests
{
    [Fact]
    public void UnknownPeriod_ReasoningSaysItIsNotIncludedAndWhy()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", "2025-11-05").Build(),
            AssessmentBuilder.Ilearn("ELA", "CP3", "Below Proficiency", "2026-02-01").Build(),
            // No period at all — mirrors the live IXL ELA export whose filename and columns carry
            // no BOY/MOY/EOY marker, so Period is never set.
            new AssessmentBuilder().Source("IXL").Subject("ELA").Proficiency("On grade").Date("2026-06-19").Build(),
        };

        var result = TierCalculationService.ComputeSubject("ELA", assessments, ruleset);

        Assert.Equal(2, result.DataPoints);   // the period-less record is not counted
        Assert.Contains("Not included in the calculation", result.Reasoning);
        Assert.Contains("the assessment period could not be identified", result.Reasoning);
        Assert.DoesNotContain("unknown_period", result.Reasoning);
    }

    [Fact]
    public void SupersededDuplicates_AreGroupedIntoOneCountedPhrase()
    {
        // Adrian Kamundia's real shape: many duplicate ILEARN CP1 rows from repeated imports.
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>();
        for (int day = 1; day <= 6; day++)
            assessments.Add(AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", $"2025-11-0{day}").Build());
        assessments.Add(AssessmentBuilder.Ilearn("ELA", "CP3", "Below Proficiency", "2026-02-01").Build());

        var result = TierCalculationService.ComputeSubject("ELA", assessments, ruleset);

        Assert.Equal(2, result.DataPoints);   // one CP1 survives, plus CP3
        Assert.Contains("5 × replaced by a more recent result for the same period", result.Reasoning);
        Assert.DoesNotContain("superseded", result.Reasoning);
    }

    [Fact]
    public void ExcludedSource_IsExplainedRatherThanTokenised()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", "2025-11-05").Build(),
            AssessmentBuilder.Ilearn("ELA", "CP3", "At Proficiency", "2026-02-01").Build(),
            AssessmentBuilder.Iread("Not Passed", "2026-03-11").Build(),
        };

        var result = TierCalculationService.ComputeSubject("ELA", assessments, ruleset);

        Assert.Contains("not part of the weighted calculation", result.Reasoning);
        Assert.DoesNotContain("source_excluded", result.Reasoning);
    }

    [Fact]
    public void NothingExcluded_AddsNoExclusionSentence()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "At Proficiency", "2025-09-01").Build(),
            AssessmentBuilder.Ilearn("Math", "CP3", "At Proficiency", "2026-02-01").Build(),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.DoesNotContain("Not included in the calculation", result.Reasoning);
    }

    [Fact]
    public void ExclusionReasonConstants_MatchTheStoredTokens()
    {
        // The frontend maps these exact strings to its own plain-English labels; renaming a token
        // here without updating EXCLUSION_LABELS in StudentProfile.tsx would show raw tokens again.
        Assert.Equal("source_excluded", TierExclusionReason.SourceExcluded);
        Assert.Equal("unrecognized_category", TierExclusionReason.UnrecognizedCategory);
        Assert.Equal("unknown_period", TierExclusionReason.UnknownPeriod);
        Assert.Equal("unknown_subject", TierExclusionReason.UnknownSubject);
        Assert.Equal("superseded", TierExclusionReason.Superseded);
    }
}
