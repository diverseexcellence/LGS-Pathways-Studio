using LgsImpact.Api.Controllers;
using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// Hand-entered records have to be normalized exactly as ingested rows are, because the tier engine
/// only ever sees the stored document: a record saved with a raw period ("Checkpoint 2") or a raw
/// subject ("Mathematics") displays on the profile and is then silently dropped from the weighted
/// score — the failure manual entry exists to prevent. These tests pin the normalization and then
/// score the result through the real engine to prove the record actually counts.
/// </summary>
public class ManualAssessmentFactoryTests
{
    private static readonly string[] Sources = StudentsController.ManualAssessmentSources;

    [Fact]
    public void Build_NormalizesPeriodAndSubject_TheSameWayIngestDoes()
    {
        var doc = ManualAssessmentFactory.Build("s-1",
            new ManualAssessmentInput("ILEARN", Subject: "Mathematics", Period: "Checkpoint 2",
                Score: 512, Proficiency: "At Proficiency", Date: "2026-01-15"),
            "admin@example.com");

        Assert.Equal("CP2", doc.Period);
        Assert.Equal("Checkpoint 2", doc.PeriodRaw);   // raw value preserved for later correction
        Assert.Equal("Math", doc.Subject);
        Assert.Equal("2026-01-15", doc.DateIso);
        Assert.False(doc.DateAmbiguous);
        Assert.Equal(ManualAssessmentFactory.SourceLabel, doc.FileName);
    }

    [Fact]
    public void Build_AppliesIreadPassFailMapping()
    {
        var doc = ManualAssessmentFactory.Build("s-1",
            new ManualAssessmentInput("IREAD", Proficiency: "Yes"), "admin@example.com");

        Assert.Equal("At Proficiency", doc.Proficiency);
        Assert.Equal("Reading", doc.Subject);   // IREAD is always Reading, with no subject supplied
    }

    [Fact]
    public void Build_LeavesPeriodNull_WhenItCannotBeResolved()
    {
        // Not an error at entry time: the engine reports it as unknown_period and the profile shows
        // "No period — not counted", which is the signal staff act on.
        var doc = ManualAssessmentFactory.Build("s-1",
            new ManualAssessmentInput("ILEARN", Period: "2025-2026", Proficiency: "At Proficiency"),
            "admin@example.com");

        Assert.Null(doc.Period);
        Assert.Equal("2025-2026", doc.PeriodRaw);
    }

    [Fact]
    public void Apply_RenormalizesAnEditedRecord()
    {
        var doc = ManualAssessmentFactory.Build("s-1",
            new ManualAssessmentInput("ILEARN", Subject: "ELA", Period: "CP1", Proficiency: "Below Proficiency"),
            "admin@example.com");

        ManualAssessmentFactory.Apply(doc,
            new ManualAssessmentInput("ILEARN", Subject: "Mathematics", Period: "Checkpoint 3",
                Proficiency: "Above Proficiency", Date: "2026-04-02"),
            "other@example.com");

        Assert.Equal("Math", doc.Subject);
        Assert.Equal("CP3", doc.Period);
        Assert.Equal("Above Proficiency", doc.Proficiency);
        Assert.Equal("2026-04-02", doc.DateIso);
    }

    [Fact]
    public void Apply_ClearsFieldsTheEditOmitted()
    {
        // An edit is a full replacement of the record's values, so clearing the score in the form
        // has to clear it on the document — a merge would leave the old score behind with no way
        // to remove it.
        var doc = ManualAssessmentFactory.Build("s-1",
            new ManualAssessmentInput("IXL", Period: "BOY", Score: 480, Proficiency: "On Grade Level",
                Date: "2025-09-01"),
            "admin@example.com");

        ManualAssessmentFactory.Apply(doc,
            new ManualAssessmentInput("IXL", Period: "BOY", Proficiency: "On Grade Level"),
            "admin@example.com");

        Assert.Null(doc.Score);
        Assert.Null(doc.Date);
        Assert.Null(doc.DateIso);
    }

    [Theory]
    [InlineData("", "source is required")]
    [InlineData("NWEA", "Unsupported assessment source")]
    // "demographics" is a valid upload type but not an assessment result — a record claiming it
    // would store an assessment no ruleset can score.
    [InlineData("demographics", "Unsupported assessment source")]
    public void Validate_RejectsAnUnusableSource(string source, string expectedFragment)
    {
        var problem = ManualAssessmentFactory.Validate(
            new ManualAssessmentInput(source, Proficiency: "At Proficiency"), Sources);

        Assert.NotNull(problem);
        Assert.Contains(expectedFragment, problem);
    }

    [Fact]
    public void Validate_RejectsARecordWithNeitherLevelNorScore()
    {
        var problem = ManualAssessmentFactory.Validate(
            new ManualAssessmentInput("ILEARN", Period: "CP1"), Sources);

        Assert.NotNull(problem);
    }

    [Fact]
    public void Validate_RejectsAnUnreadableDate()
    {
        var problem = ManualAssessmentFactory.Validate(
            new ManualAssessmentInput("ILEARN", Proficiency: "At Proficiency", Date: "last Tuesday"),
            Sources);

        Assert.NotNull(problem);
        Assert.Contains("last Tuesday", problem);
    }

    [Fact]
    public void Validate_AcceptsAScoreOnlyRecord()
        => Assert.Null(ManualAssessmentFactory.Validate(
            new ManualAssessmentInput("ILEARN", Period: "CP1", Score: 512), Sources));

    [Fact]
    public void HandEnteredRecords_AreScoredByTheEngine_LikeImportedOnes()
    {
        var ruleset = new TierRulesetConfigDocument();
        var student = new StudentDocument { Id = "s-1", StudentId = "s-1", FullName = "Test Student" };

        // Two ILEARN math checkpoints, both below proficiency (value 0) — CP1 weight 1.0,
        // CP2 weight 1.5. Score 0.00 with 2 data points, which is Tier 3 under the default
        // thresholds rather than Pending.
        var assessments = new[]
        {
            ManualAssessmentFactory.Build("s-1",
                new ManualAssessmentInput("ILEARN", "Math", "CP1", 400, "Below Proficiency", "2025-10-01"), "admin@example.com"),
            ManualAssessmentFactory.Build("s-1",
                new ManualAssessmentInput("ILEARN", "Math", "Checkpoint 2", 410, "Below Proficiency", "2026-01-15"), "admin@example.com"),
        };

        var math = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(TierStatus.SystemRecommended, math.Status);
        Assert.Equal("Tier 3", math.Tier);
        Assert.Equal(2, math.DataPoints);
        Assert.All(math.Evidence, e => Assert.True(e.Counted));
    }

    [Fact]
    public void ASingleHandEnteredRecord_LeavesTheSubjectPending()
    {
        // The 2-data-point minimum applies to manual entry too — entering one record must not
        // produce a confident tier the evidence doesn't support.
        var ruleset = new TierRulesetConfigDocument();
        var assessments = new[]
        {
            ManualAssessmentFactory.Build("s-1",
                new ManualAssessmentInput("ILEARN", "ELA", "CP1", 400, "Below Proficiency", "2025-10-01"), "admin@example.com"),
        };

        var ela = TierCalculationService.ComputeSubject("ELA", assessments, ruleset);

        Assert.Equal(TierStatus.Pending, ela.Status);
        Assert.Equal(TierPendingReason.InsufficientDataPoints, ela.PendingReason);
    }
}
