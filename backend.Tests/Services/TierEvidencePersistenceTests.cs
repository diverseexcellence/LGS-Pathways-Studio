using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using LgsImpact.Api.Tests.TestData;
using Xunit;

namespace LgsImpact.Api.Tests.Services;

/// <summary>
/// Covers what <see cref="TierCalculationService.ApplySubject"/> persists, which is separate from
/// what <see cref="TierCalculationService.ComputeSubject"/> calculates.
///
/// The bug these exist to prevent: the decision to write was made by comparing only Tier, Status,
/// Score and DataPoints, while the same branch also assigned Evidence, PendingReason and Reasoning.
/// A student's evidence can change without any of those four moving — an IREAD result arrives for a
/// subject IREAD is excluded from, a source sends a label the engine cannot map, a duplicate is
/// superseded — and every such update was silently dropped. That left students showing a bare
/// "Pending" with no reason, and blinded GET /api/upload/tier-data-quality, which reports from
/// exactly these persisted fields. Observed in the dev data: 96 students carry IXL rows with the
/// "--" placeholder, and none of those exclusions reached the report.
/// </summary>
public class TierEvidencePersistenceTests
{
    private const string Now = "2026-09-04T00:00:00.0000000Z";
    private static readonly TierRulesetConfigDocument Ruleset = RulesetFixture.Default();

    private static SubjectTier FreshPending() => new(); // Status "Pending", no evidence

    // ── the three reproductions from the QA pass ─────────────────────────────────

    [Fact]
    public void ExcludedOnlyEvidence_IsPersistedWithItsPendingReason()
    {
        // A student whose only reading result is IREAD: nothing counts, so Tier/Status/Score/
        // DataPoints all match a never-computed SubjectTier exactly. Before the fix this was
        // treated as "no change" and the student kept pendingReason = null, reasoning = null and
        // an empty evidence list, with no way to tell "no data" from "data we could not use".
        var target = FreshPending();
        var computed = TierCalculationService.ComputeSubject("ELA",
            [AssessmentBuilder.Iread("Below Proficiency", "2026-08-22")], Ruleset);

        var persisted = TierCalculationService.ApplySubject(target, computed, "2.0", Now, out var tierMoved);

        Assert.True(persisted);
        Assert.False(tierMoved); // the recommendation itself did not move — so this must not audit
        Assert.Equal("all_evidence_excluded", target.PendingReason);
        Assert.NotNull(target.Reasoning);
        var evidence = Assert.Single(target.Evidence);
        Assert.Equal("IREAD", evidence.Source);
        Assert.False(evidence.Counted);
        Assert.Equal(TierExclusionReason.SourceExcluded, evidence.ExclusionReason);
    }

    [Fact]
    public void LaterExcludedResult_IsAddedToAnAlreadyPersistedEvidenceTrail()
    {
        // Acadience lands first and is persisted normally. IREAD then arrives: still one counted
        // data point, same provisional score, still Pending — so the four compared fields are
        // unchanged and the new evidence row was previously discarded.
        var target = FreshPending();
        var acadience = AssessmentBuilder.Acadience("BOY", "At Benchmark", "2026-08-18").Build();

        var first = TierCalculationService.ComputeSubject("ELA", [acadience], Ruleset);
        TierCalculationService.ApplySubject(target, first, "2.0", Now, out _);
        Assert.Single(target.Evidence);

        var second = TierCalculationService.ComputeSubject("ELA",
            [acadience, AssessmentBuilder.Iread("Below Proficiency", "2026-08-22")], Ruleset);
        var persisted = TierCalculationService.ApplySubject(target, second, "2.0", Now, out var tierMoved);

        Assert.True(persisted);
        Assert.False(tierMoved);
        Assert.Equal(2, target.Evidence.Count);
        Assert.Contains(target.Evidence, e => e.Source == "IREAD" && !e.Counted);
        Assert.Equal(1, target.DataPoints); // the excluded row must not become a data point
    }

    [Fact]
    public void IxlPlaceholder_IsPersistedAsANoResultExclusion()
    {
        // The IXL "--" placeholder: the diagnostic was never completed. Correctly excluded from
        // the score, but the administrator has to be able to see it — and to see that the cause is
        // a missing test rather than a value the engine could not read.
        var target = FreshPending();
        var computed = TierCalculationService.ComputeSubject("ELA",
            [AssessmentBuilder.Ixl("ELA", "BOY", "--", "2026-08-25")], Ruleset);

        TierCalculationService.ApplySubject(target, computed, "2.0", Now, out _);

        var evidence = Assert.Single(target.Evidence);
        Assert.Equal(TierExclusionReason.NoResultReported, evidence.ExclusionReason);
        Assert.Equal("all_evidence_excluded", target.PendingReason);
    }

    [Fact]
    public void GenuinelyUnreadableLabel_IsPersistedAsAnUnrecognizedCategoryExclusion()
    {
        // A value that is present and non-placeholder but that the ruleset cannot map — the case
        // that actually warrants a data-quality follow-up with the source.
        var target = FreshPending();
        var computed = TierCalculationService.ComputeSubject("ELA",
            [AssessmentBuilder.Ixl("ELA", "BOY", "Watch", "2026-08-25")], Ruleset);

        TierCalculationService.ApplySubject(target, computed, "2.0", Now, out _);

        var evidence = Assert.Single(target.Evidence);
        Assert.Equal(TierExclusionReason.UnrecognizedCategory, evidence.ExclusionReason);
        Assert.Equal("all_evidence_excluded", target.PendingReason);
    }

    // ── the write must still be skipped when genuinely nothing changed ───────────

    [Fact]
    public void RecomputingIdenticalInput_PersistsNothingTheSecondTime()
    {
        // The dirty check still has to work, or every recalculation pass rewrites every student.
        var target = FreshPending();
        AssessmentDocument[] assessments = [
            AssessmentBuilder.Ilearn("ELA", "CP1", "At Proficiency", "2026-08-20"),
            AssessmentBuilder.Acadience("BOY", "Above Benchmark", "2026-08-18"),
        ];
        var computed = TierCalculationService.ComputeSubject("ELA", assessments, Ruleset);

        Assert.True(TierCalculationService.ApplySubject(target, computed, "2.0", Now, out var firstMoved));
        Assert.True(firstMoved);

        var again = TierCalculationService.ComputeSubject("ELA", assessments, Ruleset);
        Assert.False(TierCalculationService.ApplySubject(target, again, "2.0", Now, out var secondMoved));
        Assert.False(secondMoved);
    }

    [Fact]
    public void RealTierMovement_ReportsTierMovedSoItIsAudited()
    {
        var target = FreshPending();
        var computed = TierCalculationService.ComputeSubject("ELA", [
            AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", "2026-08-20"),
            AssessmentBuilder.Acadience("BOY", "Below Benchmark", "2026-08-18"),
        ], Ruleset);

        Assert.True(TierCalculationService.ApplySubject(target, computed, "2.0", Now, out var tierMoved));
        Assert.True(tierMoved);
        Assert.Equal("Tier 3", target.Tier);
        Assert.Equal(TierStatus.SystemRecommended, target.Status);
    }

    [Fact]
    public void AdminOverriddenSubject_KeepsTheTier_AndRefreshesScoreAndCount()
    {
        // The override is the recommendation, so the tier and who set it stay. The score and the
        // assessment count are a reading of the current evidence and have to move with it, or an
        // overridden subject shows a stale caption after records are added or removed.
        var target = new SubjectTier
        {
            Tier = "Tier 1",
            Status = TierStatus.AdminOverride,
            Score = 3.00,
            DataPoints = 2,
            OverriddenBy = "velvet@lgs.local",
            OverriddenAt = Now,
            OverrideExplanation = "Held at Tier 1 for a documented reason",
        };
        var computed = TierCalculationService.ComputeSubject("ELA", [
            AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", "2026-08-20"),
            AssessmentBuilder.Acadience("BOY", "Below Benchmark", "2026-08-18"),
        ], Ruleset);

        Assert.True(TierCalculationService.ApplySubject(target, computed, "2.0", Now, out var tierMoved));
        Assert.False(tierMoved);
        Assert.Equal("Tier 1", target.Tier);
        Assert.Equal(TierStatus.AdminOverride, target.Status);
        Assert.Equal("velvet@lgs.local", target.OverriddenBy);
        Assert.Equal("Held at Tier 1 for a documented reason", target.OverrideExplanation);
        Assert.Equal(computed.Score, target.Score);
        Assert.Equal(computed.DataPoints, target.DataPoints);
        Assert.NotEmpty(target.Evidence);
        Assert.NotEqual(3.00, target.Score);
    }

    [Fact]
    public void ForcedApply_ReplacesAdminOverrideWithSystemRecommendation()
    {
        var target = new SubjectTier
        {
            Tier = "Tier 1",
            Status = TierStatus.AdminOverride,
            OverriddenBy = "velvet@lgs.local",
            OverriddenAt = Now,
            OverrideExplanation = "Held at Tier 1 for a documented reason",
        };
        var computed = TierCalculationService.ComputeSubject("ELA", [
            AssessmentBuilder.Ilearn("ELA", "CP1", "Below Proficiency", "2026-08-20"),
            AssessmentBuilder.Acadience("BOY", "Below Benchmark", "2026-08-18"),
        ], Ruleset);

        Assert.True(TierCalculationService.ApplySubject(target, computed, "2.0", Now, out var tierMoved, force: true));
        Assert.True(tierMoved);
        Assert.Equal(TierStatus.SystemRecommended, target.Status);
        Assert.Equal("Tier 3", target.Tier);
        Assert.Null(target.OverriddenBy);
        Assert.Null(target.OverrideExplanation);
        Assert.NotEmpty(target.Evidence);
    }

    [Fact]
    public void RulesetVersionChangeAlone_IsPersisted()
    {
        // A re-versioned ruleset that happens to produce the same numbers still has to update the
        // stamp, or the reasoning string cites a version the record no longer reflects.
        var target = FreshPending();
        AssessmentDocument[] assessments = [
            AssessmentBuilder.Ilearn("ELA", "CP1", "At Proficiency", "2026-08-20"),
            AssessmentBuilder.Acadience("BOY", "At Benchmark", "2026-08-18"),
        ];
        var computed = TierCalculationService.ComputeSubject("ELA", assessments, Ruleset);
        TierCalculationService.ApplySubject(target, computed, "2.0", Now, out _);

        var recomputed = TierCalculationService.ComputeSubject("ELA", assessments, Ruleset);
        Assert.True(TierCalculationService.ApplySubject(target, recomputed, "2.1", Now, out var tierMoved));
        Assert.False(tierMoved);
        Assert.Equal("2.1", target.RulesetVersion);
    }
}
