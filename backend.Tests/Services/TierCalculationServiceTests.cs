using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using LgsImpact.Api.Tests.TestData;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

public class TierCalculationServiceTests
{
    private static StudentDocument Student(string grade = "3") => new()
    {
        Id = "s-test", StudentId = "s-test", FullName = "Test Student", ClassGroup = "Unassigned", Grade = grade,
    };

    // ── Spec §10.1 Example A — ILEARN Math only ──────────────────────────────────
    [Fact]
    public void ExampleA_DecliningCheckpoints_YieldsTier3AtPoint78()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "At Proficiency", "2025-09-01"),
            AssessmentBuilder.Ilearn("Math", "CP2", "Approaching Proficiency", "2025-11-01"),
            AssessmentBuilder.Ilearn("Math", "CP3", "Below Proficiency", "2026-02-01"),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(3, result.DataPoints);
        Assert.Equal(3.5, result.WeightedSum, 3);
        Assert.Equal(4.5, result.WeightSum, 3);
        Assert.Equal(0.78, result.Score);
        Assert.Equal("Tier 3", result.Tier);
        Assert.Equal("System Recommended", result.Status);
    }

    // ── Spec §10.2 Example B — improving performance ─────────────────────────────
    // The client-visible bug: the OLD engine reads only the latest checkpoint (At Proficiency)
    // and would call this Tier 1. The spec's weighted average is Tier 2.
    [Fact]
    public void ExampleB_ImprovingCheckpoints_YieldsTier2AtPoint22()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "Below Proficiency", "2025-09-01"),
            AssessmentBuilder.Ilearn("Math", "CP2", "Approaching Proficiency", "2025-11-01"),
            AssessmentBuilder.Ilearn("Math", "CP3", "At Proficiency", "2026-02-01"),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(5.5, result.WeightedSum, 3);
        Assert.Equal(4.5, result.WeightSum, 3);
        Assert.Equal(1.22, result.Score);
        Assert.Equal("Tier 2", result.Tier);
    }

    // ── Spec §10.3 Example C — missing checkpoint excluded, not coerced to 0 ────
    [Fact]
    public void ExampleC_MissingCheckpoint_ExcludedFromBothSums()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "At Proficiency", "2025-09-01"),
            // CP2 missing entirely
            AssessmentBuilder.Ilearn("Math", "CP3", "Approaching Proficiency", "2026-02-01"),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(2, result.DataPoints);
        Assert.Equal(4.0, result.WeightedSum, 3);
        Assert.Equal(3.0, result.WeightSum, 3); // regression guard: must NOT be 4.5 (CP2 coerced to weight w/ value 0)
        Assert.Equal(1.33, result.Score);
        Assert.Equal("Tier 2", result.Tier);
    }

    // ── Spec §10.5 Example E — IXL EOY contribution and the 2-point minimum ─────
    [Fact]
    public void ExampleE_IxlEoyAlone_ContributesFourButStaysPending()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument> { AssessmentBuilder.Ixl("Math", "EOY", "On grade", "2026-05-01") };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        var evidence = Assert.Single(result.Evidence);
        Assert.True(evidence.Counted);
        Assert.Equal(2, evidence.Value);
        Assert.Equal(2.0, evidence.Weight);

        Assert.Equal("Pending", result.Status);
        Assert.Equal(TierPendingReason.InsufficientDataPoints, result.PendingReason);
        Assert.Equal(1, result.DataPoints);
        Assert.Equal(2.0, result.Score); // provisional score: weighted contribution 4.0 / weight 2.0
    }

    [Fact]
    public void ExampleE_IxlEoyAndMoy_YieldsTier1AtBoundary()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ixl("Math", "MOY", "On grade", "2026-01-01"),
            AssessmentBuilder.Ixl("Math", "EOY", "On grade", "2026-05-01"),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(7.0, result.WeightedSum, 3);
        Assert.Equal(3.5, result.WeightSum, 3);
        Assert.Equal(2.00, result.Score);
        Assert.Equal("Tier 1", result.Tier); // pins the >= 2.00 boundary
    }

    // ── Minimum data points / Pending reasons ────────────────────────────────────

    [Fact]
    public void NoAssessments_PendingWithNoAssessmentsReason()
    {
        var result = TierCalculationService.ComputeSubject("Math", new List<AssessmentDocument>(), RulesetFixture.Default());
        Assert.Equal("Pending", result.Status);
        Assert.Equal(TierPendingReason.NoAssessments, result.PendingReason);
        Assert.Null(result.Score);
    }

    [Fact]
    public void OnlyIreadEvidence_AllExcluded()
    {
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Iread("At Proficiency", "2026-01-01"),
            AssessmentBuilder.Iread("Below Proficiency", "2026-05-01"),
        };
        var result = TierCalculationService.ComputeSubject("ELA", assessments, RulesetFixture.Default());

        Assert.Equal("Pending", result.Status);
        Assert.Equal(TierPendingReason.AllEvidenceExcluded, result.PendingReason);
        Assert.Equal(0, result.DataPoints);
        Assert.All(result.Evidence, e => Assert.Equal("source_excluded", e.ExclusionReason));
    }

    [Fact]
    public void MinDataPointsConfigurable_OnePointTiersWhenMinIsOne()
    {
        var ruleset = RulesetFixture.Default();
        ruleset.MinDataPoints = 1;
        var assessments = new List<AssessmentDocument> { AssessmentBuilder.Ixl("Math", "EOY", "On grade", "2026-05-01") };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal("System Recommended", result.Status);
        Assert.Equal("Tier 1", result.Tier);
    }

    // ── Independent subjects (TR-010) ────────────────────────────────────────────

    [Fact]
    public void Subjects_AreFullyIndependent_MathTieredElaPending()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "At Proficiency", "2025-09-01"),
            AssessmentBuilder.Ilearn("Math", "CP2", "Approaching Proficiency", "2025-11-01"),
            AssessmentBuilder.Acadience("BOY", "At Benchmark", "2025-09-01"), // only 1 ELA point
        };

        var computation = TierCalculationService.ComputeAll(Student(), assessments, ruleset);

        Assert.Equal("System Recommended", computation.Math.Status);
        Assert.Equal("Pending", computation.Ela.Status);
    }

    [Fact]
    public void KindergartenAcadienceOnly_ElaTieredMathPending()
    {
        // K-2 Acadience-only case: the OLD engine used Reading as a Math proxy here, fabricating
        // a Math signal that doesn't exist. The new engine leaves Math Pending.
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Acadience("BOY", "At Benchmark", "2025-09-01"),
            AssessmentBuilder.Acadience("MOY", "Above Benchmark", "2026-01-01"),
        };

        var computation = TierCalculationService.ComputeAll(Student("0"), assessments, ruleset);

        Assert.Equal("System Recommended", computation.Ela.Status);
        Assert.Equal("Pending", computation.Math.Status);
        Assert.Equal(TierPendingReason.NoAssessments, computation.Math.PendingReason);
    }

    // ── Percentile fallback removed (TR-003) ─────────────────────────────────────

    [Fact]
    public void NoPercentileFallback_UnrecognizedCategoryExcludedEvenWithPercentile()
    {
        var ruleset = RulesetFixture.Default();
        var a = AssessmentBuilder.Ilearn("Math", "CP1", null, "2026-01-01").RawField("Reading Percentile", "85").Build();

        var result = TierCalculationService.ComputeSubject("Math", new List<AssessmentDocument> { a }, ruleset);

        var evidence = Assert.Single(result.Evidence);
        Assert.False(evidence.Counted);
        Assert.Equal("unrecognized_category", evidence.ExclusionReason);
    }

    // ── Latest-wins dedup across mixed date formats (the sort bug) ───────────────

    [Fact]
    public void LatestWins_UsesChronologicalOrder_NotLexicographicDateString()
    {
        var ruleset = RulesetFixture.Default();
        // "22/8/2025" sorts AFTER "3/1/2026" lexicographically, but is chronologically earlier.
        // DateIso must be authoritative; asserts the correct (2026) record wins.
        var older = new AssessmentDocument
        {
            Id = "a-older", StudentId = "s-test", UploadType = "ILEARN", Subject = "Math", FileName = "f.csv",
            Period = "CP2", Proficiency = "Below Proficiency", Date = "22/8/2025", DateIso = "2025-08-22",
            UploadedAt = "2025-08-23T00:00:00.0000000Z",
        };
        var newer = new AssessmentDocument
        {
            Id = "a-newer", StudentId = "s-test", UploadType = "ILEARN", Subject = "Math", FileName = "f.csv",
            Period = "CP2", Proficiency = "At Proficiency", Date = "3/1/2026", DateIso = "2026-01-03",
            UploadedAt = "2026-01-04T00:00:00.0000000Z",
        };

        var result = TierCalculationService.ComputeSubject("Math", new List<AssessmentDocument> { older, newer }, ruleset);

        var counted = Assert.Single(result.Evidence, e => e.Counted);
        Assert.Equal("At Proficiency", counted.Category);
        var superseded = Assert.Single(result.Evidence, e => !e.Counted);
        Assert.Equal("superseded", superseded.ExclusionReason);
    }

    // ── Thresholds ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.00, "Tier 3")]
    [InlineData(0.99, "Tier 3")]
    [InlineData(1.00, "Tier 2")]
    [InlineData(1.99, "Tier 2")]
    [InlineData(2.00, "Tier 1")]
    [InlineData(3.00, "Tier 1")]
    public void Thresholds_MapScoreToExpectedTier(double score, string expectedTier)
    {
        var ruleset = RulesetFixture.Default();
        var tier = ruleset.TierThresholds
            .OrderByDescending(t => t.MinScoreInclusive)
            .First(t => score >= t.MinScoreInclusive).Tier;
        Assert.Equal(expectedTier, tier);
    }

    // ── Spring ILEARN weight dominance ───────────────────────────────────────────

    [Fact]
    public void SpringIlearn_HasHighestWeight_DominatesCp1()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "Above Proficiency", "2025-09-01"), // 3 x 1.0 = 3.0
            AssessmentBuilder.Ilearn("Math", "SPRING", "Below Proficiency", "2026-05-01"), // 0 x 2.5 = 0.0
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);

        Assert.Equal(3.0, result.WeightedSum, 3);
        Assert.Equal(3.5, result.WeightSum, 3);
        Assert.Equal(0.86, result.Score); // pulled toward "below" by Spring's larger weight
    }

    // ── Unknown period excluded by default, includable via config ───────────────

    [Fact]
    public void UnknownPeriod_ExcludedByDefault_IncludedWhenConfigured()
    {
        var assessments = new List<AssessmentDocument>
        {
            new() { Id = "a1", StudentId = "s-test", UploadType = "ILEARN", Subject = "Math",
                    FileName = "f.csv", Period = null, Proficiency = "At Proficiency", DateIso = "2026-01-01" },
        };

        var excluded = TierCalculationService.ComputeSubject("Math", assessments, RulesetFixture.Default());
        Assert.Equal("unknown_period", Assert.Single(excluded.Evidence).ExclusionReason);

        var withFallback = RulesetFixture.Default();
        withFallback.UnknownPeriodWeight = 1.0;
        var included = TierCalculationService.ComputeSubject("Math", assessments, withFallback);
        Assert.True(Assert.Single(included.Evidence).Counted);
    }

    // ── Category label tolerance ─────────────────────────────────────────────────

    [Theory]
    [InlineData("AT PROFICIENCY")]
    [InlineData("At Proficiency ")]
    [InlineData("At Proficiency (Level 3)")]
    public void CategoryResolution_IsCaseAndWhitespaceTolerant(string label)
    {
        var ruleset = RulesetFixture.Default();
        Assert.True(PerformanceLevelNormalizer.TryResolve("ILEARN", label, ruleset, out var value));
        Assert.Equal(2, value);
    }

    // ── Acadience routes to ELA, never Math ──────────────────────────────────────

    [Fact]
    public void Acadience_RoutesToElaOnly()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Acadience("BOY", "At Benchmark", "2025-09-01"),
            AssessmentBuilder.Acadience("MOY", "At Benchmark", "2026-01-01"),
        };

        var math = TierCalculationService.ComputeSubject("Math", assessments, ruleset);
        var ela = TierCalculationService.ComputeSubject("ELA", assessments, ruleset);

        Assert.Equal("no_assessments", math.PendingReason);
        Assert.Equal("System Recommended", ela.Status);
    }

    // ── No combined overall tier exposed ─────────────────────────────────────────

    [Fact]
    public void ComputeAll_ExposesOnlyElaAndMath_NoCombinedTier()
    {
        var computation = TierCalculationService.ComputeAll(Student(), new List<AssessmentDocument>(), RulesetFixture.Default());
        // The type itself proves this: StudentTierComputation only has Ela/Math/RulesetVersion.
        Assert.NotNull(computation.Ela);
        Assert.NotNull(computation.Math);
    }

    // ── Ruleset override changes the outcome ─────────────────────────────────────

    [Fact]
    public void RulesetOverride_ShiftedThresholds_ChangeExampleBFromTier2ToTier1()
    {
        var ruleset = RulesetFixture.Default();
        ruleset.TierThresholds = new List<TierThreshold> { new("Tier 1", 1.20), new("Tier 2", 1.00), new("Tier 3", 0.00) };
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "Below Proficiency", "2025-09-01"),
            AssessmentBuilder.Ilearn("Math", "CP2", "Approaching Proficiency", "2025-11-01"),
            AssessmentBuilder.Ilearn("Math", "CP3", "At Proficiency", "2026-02-01"),
        };

        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);
        Assert.Equal("Tier 1", result.Tier); // score 1.22 now clears the lowered 1.20 boundary
    }

    // ── Reasoning string is deterministic and versioned ──────────────────────────

    [Fact]
    public void Reasoning_ContainsRulesetVersion()
    {
        var ruleset = RulesetFixture.Default();
        var assessments = new List<AssessmentDocument>
        {
            AssessmentBuilder.Ilearn("Math", "CP1", "At Proficiency", "2025-09-01"),
            AssessmentBuilder.Ilearn("Math", "CP2", "At Proficiency", "2025-11-01"),
        };
        var result = TierCalculationService.ComputeSubject("Math", assessments, ruleset);
        Assert.Contains($"Ruleset v{ruleset.RulesetVersion}", result.Reasoning);
    }
}
