using LgsImpact.Api.Services;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// Pins the date and period conventions against values taken from LGS's actual exports. Both were
/// wrong in ways nothing detected: Acadience was assumed day-first (it is month-first), and the
/// ILEARN checkpoint was read from "Test OppNumber" ("First Assessment") instead of "Test Reason"
/// ("ILEARN Checkpoint 3"), so an export whose filename lacked a checkpoint lost its period.
/// </summary>
public class SourceDateAndPeriodTests
{
    // ── Date convention: every LGS source is month-first ────────────────────────────

    [Theory]
    // alo_reading_pm_data_2025-2026.csv — real values, 2-digit year. These are the ones the old
    // Acadience day-first special case moved by whole months.
    [InlineData("11/4/25",  "Acadience", "2025-11-04")]
    [InlineData("12/5/25",  "Acadience", "2025-12-05")]
    [InlineData("1/7/26",   "Acadience", "2026-01-07")]
    [InlineData("11/18/25", "Acadience", "2025-11-18")]
    [InlineData("9/11/25",  "Acadience", "2025-09-11")]
    // ILEARN Checkpoint 1 / Checkpoint 3 exports
    [InlineData("10/22/2025", "ILEARN", "2025-10-22")]
    [InlineData("02/25/2026", "ILEARN", "2026-02-25")]
    [InlineData("11/5/2025",  "ILEARN", "2025-11-05")]
    [InlineData("11/4/2025",  "ILEARN", "2025-11-04")]
    // IXL wraps its dates in parentheses
    [InlineData("(10/29/2025)", "IXL", "2025-10-29")]
    [InlineData("(11/06/2025)", "IXL", "2025-11-06")]
    public void AllSources_ParseMonthFirst(string raw, string source, string expected)
    {
        var iso = AssessmentNormalization.TryParseFlexibleDate(raw, source, out _);
        Assert.Equal(expected, iso);
    }

    [Fact]
    public void Acadience_IsNoLongerTreatedAsDayFirst()
    {
        // The regression guard. Under the old special case this returned "2025-04-11".
        var iso = AssessmentNormalization.TryParseFlexibleDate("11/4/25", "Acadience", out var ambiguous);
        Assert.Equal("2025-11-04", iso);
        Assert.True(ambiguous, "both segments are <= 12, so the value is still reported as ambiguous");
    }

    [Fact]
    public void DateFormat_DoesNotDependOnTheSource()
    {
        // Same string, four sources, one answer — no per-source day/month divergence remains.
        foreach (var source in new[] { "ILEARN", "IXL", "Acadience", "IREAD" })
            Assert.Equal("2025-11-04", AssessmentNormalization.TryParseFlexibleDate("11/4/2025", source, out _));
    }

    [Theory]
    [InlineData("22/8/2025", "2025-08-22")]   // first segment > 12 — unambiguously a day
    [InlineData("2025-08-19", "2025-08-19")]  // ISO, as in 2526_BOY_ALO.csv
    public void UnambiguousForms_StillParseCorrectly(string raw, string expected)
        => Assert.Equal(expected, AssessmentNormalization.TryParseFlexibleDate(raw, "Acadience", out _));

    [Fact]
    public void MissingDate_IsNull()
    {
        // 82 of 206 rows in the IXL BOY export have "--" rather than a date.
        Assert.Null(AssessmentNormalization.TryParseFlexibleDate("--", "IXL", out _));
        Assert.Null(AssessmentNormalization.TryParseFlexibleDate("", "IXL", out _));
        Assert.Null(AssessmentNormalization.TryParseFlexibleDate(null, "IXL", out _));
    }

    // ── ILEARN checkpoint resolution from the file's own columns ────────────────────

    [Theory]
    [InlineData("ILEARN Checkpoint 3", "CP3")]
    [InlineData("ILEARN Checkpoint 1 (Opportunity 1)", "CP1")]
    [InlineData("ILEARN Mathematics Grade 6 Checkpoint 3, Opp 1:  Rates, Ratios, & Proportions", "CP3")]
    [InlineData("ILEARN English/Language Arts G3 Checkpoint 1, Opp 1:  Reading Foundations & Fiction", "CP1")]
    [InlineData("ILEARN Mathematics G3 Checkpoint 2, Opp 1:  Multiplication, Division, & Fractions", "CP2")]
    public void RealTestReasonAndTestNameValues_ResolveTheCheckpoint(string columnValue, string expected)
        => Assert.Equal(expected, AssessmentNormalization.NormalizeIlearnPeriod(columnValue, ""));

    [Fact]
    public void TestOppNumberValue_CarriesNoCheckpoint()
    {
        // "First Assessment" identifies the attempt, not the checkpoint. Probing this column first
        // is what caused the period to be lost.
        Assert.Null(AssessmentNormalization.NormalizeIlearnPeriod("First Assessment", ""));
    }

    [Fact]
    public void AdaZelFileName_CarriesNoCheckpoint_SoTheColumnIsTheOnlySource()
    {
        // The export as delivered by the state. Before the column fix this file had to be renamed
        // by hand to be scored at all.
        const string realName = "LibertyGroveSchools_Page1_ADA-ZEL_StudentData_150626 PM.csv";
        Assert.Null(AssessmentNormalization.NormalizeIlearnPeriod(null, realName));
        Assert.Equal("CP3", AssessmentNormalization.NormalizeIlearnPeriod("ILEARN Checkpoint 3", realName));
    }

    [Fact]
    public void SchoolYearValue_DoesNotMasqueradeAsAPeriod()
        => Assert.Null(AssessmentNormalization.NormalizeIlearnPeriod("2025-2026", ""));

    [Fact]
    public void ColumnBeatsFileName_WhenTheyDisagree()
    {
        // A mistyped manual rename must not override what the file itself states.
        Assert.Equal("CP3", AssessmentNormalization.NormalizeIlearnPeriod(
            "ILEARN Checkpoint 3", "LibertyGroveSchools_ADA-ZEL_StudentData_Checkpoint2.csv"));
    }
}
