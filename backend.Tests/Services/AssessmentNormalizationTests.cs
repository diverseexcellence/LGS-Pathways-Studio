using LgsImpact.Api.Services;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

public class AssessmentNormalizationTests
{
    [Theory]
    [InlineData("CP1", "CP1")]
    [InlineData("Checkpoint 2", "CP2")]
    [InlineData("Checkpoint3", "CP3")]
    [InlineData("Spring", "SPRING")]
    [InlineData("Summative Assessment", "SPRING")]
    public void NormalizeIlearnPeriod_ResolvesFromColumnValue(string column, string expected)
        => Assert.Equal(expected, AssessmentNormalization.NormalizeIlearnPeriod(column, "file.csv"));

    [Fact]
    public void NormalizeIlearnPeriod_ChecksCheckpointBeforeSpring()
    {
        // A file can legitimately be named "ILEARN-Checkpoint2-Spring2026" — CP2 must win, not SPRING.
        var result = AssessmentNormalization.NormalizeIlearnPeriod(null, "ILEARN-Checkpoint2-Spring2026.csv");
        Assert.Equal("CP2", result);
    }

    [Fact]
    public void NormalizeIlearnPeriod_UnresolvedReturnsNull_NotRawValue()
    {
        var result = AssessmentNormalization.NormalizeIlearnPeriod("2025-2026", "ILEARN-Results.csv");
        Assert.Null(result);
    }

    [Fact]
    public void NormalizeIxlPeriod_ResolvesFromFilenameConvention()
    {
        var result = AssessmentNormalization.NormalizeIxlPeriod(null, "LevelUp-Diagnostic-Results-EOY-Math-LevelUp-Benchmark-By-Student-2026-06-19.csv");
        Assert.Equal("EOY", result);
    }

    [Theory]
    [InlineData("BOY")]
    [InlineData("Beginning")]
    public void NormalizeIxlPeriod_Boy(string column) => Assert.Equal("BOY", AssessmentNormalization.NormalizeIxlPeriod(column, "file.csv"));

    [Theory]
    [InlineData("BOY", "BOY")]
    [InlineData("Beginning", "BOY")]
    [InlineData("MOY", "MOY")]
    [InlineData("Middle", "MOY")]
    [InlineData("EOY", "EOY")]
    [InlineData("End", "EOY")]
    public void NormalizeAcadiencePeriod_ResolvesFromColumnOrFilename(string column, string expected)
        => Assert.Equal(expected, AssessmentNormalization.NormalizeAcadiencePeriod(column, "file.csv"));

    [Theory]
    [InlineData("Did Not Pass", "Below Proficiency")]
    [InlineData("Fail", "Below Proficiency")]
    [InlineData("Passed", "At Proficiency")]
    [InlineData("P", "At Proficiency")]
    [InlineData("Waived", "Waived")]
    [InlineData("Exempt", "Exempt")]
    public void NormalizeIReadProficiency_MapsPassFailStrings(string raw, string expected)
        => Assert.Equal(expected, AssessmentNormalization.NormalizeIReadProficiency(raw));

    // ── Date parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void TryParseFlexibleDate_StripsParensAndParsesIxlDate()
    {
        var result = AssessmentNormalization.TryParseFlexibleDate("(11/13/2025)", "IXL", out var ambiguous);
        Assert.Equal("2025-11-13", result);
        Assert.False(ambiguous); // day segment > 12, unambiguous
    }

    [Fact]
    public void TryParseFlexibleDate_AcadienceDayFirst()
    {
        var result = AssessmentNormalization.TryParseFlexibleDate("22/8/2025", "Acadience", out var ambiguous);
        Assert.Equal("2025-08-22", result);
        Assert.False(ambiguous); // day segment > 12, unambiguous
    }

    [Fact]
    public void TryParseFlexibleDate_AlreadyIso()
    {
        var result = AssessmentNormalization.TryParseFlexibleDate("2026-03-01", "ILEARN", out var ambiguous);
        Assert.Equal("2026-03-01", result);
        Assert.False(ambiguous);
    }

    [Fact]
    public void TryParseFlexibleDate_AmbiguousBothSegmentsLow_FlagsAmbiguous()
    {
        var result = AssessmentNormalization.TryParseFlexibleDate("5/6/2026", "IXL", out var ambiguous);
        Assert.True(ambiguous);
        Assert.Equal("2026-05-06", result); // IXL defaults month-first
    }

    [Fact]
    public void TryParseFlexibleDate_AmbiguousAcadienceDefaultsDayFirst()
    {
        var result = AssessmentNormalization.TryParseFlexibleDate("5/6/2026", "Acadience", out var ambiguous);
        Assert.True(ambiguous);
        Assert.Equal("2026-06-05", result); // Acadience defaults day-first
    }

    [Fact]
    public void TryParseFlexibleDate_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(AssessmentNormalization.TryParseFlexibleDate(null, "ILEARN", out _));
        Assert.Null(AssessmentNormalization.TryParseFlexibleDate("", "ILEARN", out _));
    }
}
