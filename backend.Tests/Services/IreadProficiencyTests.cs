using LgsImpact.Api.Services;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// IREAD pass/fail normalization. Indiana's IREAD export carries its pass column as "Yes"/"No",
/// which is what every IREAD record in the dev data holds — 26 "No" and 15 "Yes". Those two values
/// were not among the recognised forms, so they fell through to the raw pass-through and surfaced
/// on the student profile as a bare "Yes"/"No" instead of a proficiency label. IREAD is excluded
/// from the weighted tier calculation (AC-09), so this is display consistency, not scoring.
/// </summary>
public class IreadProficiencyTests
{
    [Theory]
    [InlineData("No")]
    [InlineData("no")]
    [InlineData("N")]
    [InlineData("Did Not Pass")]
    [InlineData("Not Passed")]
    [InlineData("Fail")]
    [InlineData("F")]
    public void FailingForms_MapToBelowProficiency(string raw)
    {
        Assert.Equal("Below Proficiency", AssessmentNormalization.NormalizeIReadProficiency(raw));
    }

    [Theory]
    [InlineData("Yes")]
    [InlineData("yes")]
    [InlineData("Y")]
    [InlineData("Passed")]
    [InlineData("Pass")]
    [InlineData("P")]
    public void PassingForms_MapToAtProficiency(string raw)
    {
        Assert.Equal("At Proficiency", AssessmentNormalization.NormalizeIReadProficiency(raw));
    }

    [Theory]
    [InlineData("Waived", "Waived")]
    [InlineData("Exempt", "Exempt")]
    public void WaivedAndExempt_KeepTheirOwnLabels(string raw, string expected)
    {
        Assert.Equal(expected, AssessmentNormalization.NormalizeIReadProficiency(raw));
    }

    [Fact]
    public void UnknownValue_IsPassedThroughRatherThanGuessed()
    {
        // Better a visible unexpected label than a silently invented one.
        Assert.Equal("Retested", AssessmentNormalization.NormalizeIReadProficiency("Retested"));
        Assert.Null(AssessmentNormalization.NormalizeIReadProficiency(null));
    }
}
