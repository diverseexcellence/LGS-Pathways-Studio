using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

public interface ITierCalculationService
{
    /// <summary>
    /// Computes ELA and Math tier recommendations independently for the student based on their
    /// current assessments, updates the StudentDocument in Cosmos, and writes an audit entry when
    /// something actually changed. A subject whose status is "Admin Override" is never touched.
    /// </summary>
    Task ComputeAndApplyAsync(StudentDocument student, int systemAdminId = 0, string systemAdminEmail = "system");

    /// <summary>
    /// Batched version for bulk recalculation (post-upload, recalculate-all). Fetches all
    /// assessments and the ruleset once instead of once per student.
    /// </summary>
    Task<int> ComputeAndApplyBatchAsync(IReadOnlyList<StudentDocument> students, int systemAdminId = 0, string systemAdminEmail = "system");
}

public class TierCalculationService(
    ICosmosDbService cosmos,
    IAuditService audit,
    ILogger<TierCalculationService> logger) : ITierCalculationService
{
    // ── Public entry points ─────────────────────────────────────────────────────

    public async Task ComputeAndApplyAsync(StudentDocument student, int systemAdminId = 0, string systemAdminEmail = "system")
    {
        var assessments = await cosmos.GetAssessmentsAsync(student.StudentId);
        var ruleset = await cosmos.GetTierRulesetConfigAsync();
        await ApplyOneAsync(student, assessments, ruleset, systemAdminId, systemAdminEmail);
    }

    public async Task<int> ComputeAndApplyBatchAsync(IReadOnlyList<StudentDocument> students, int systemAdminId = 0, string systemAdminEmail = "system")
    {
        var ruleset = await cosmos.GetTierRulesetConfigAsync();
        var allAssessments = await cosmos.GetAllAssessmentsAsync();
        var byStudent = allAssessments.GroupBy(a => a.StudentId).ToDictionary(g => g.Key, g => g.ToList());

        var updated = 0;
        foreach (var student in students)
        {
            try
            {
                byStudent.TryGetValue(student.StudentId, out var assessments);
                var changed = await ApplyOneAsync(student, assessments ?? new List<AssessmentDocument>(), ruleset, systemAdminId, systemAdminEmail);
                if (changed) updated++;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tier recalculation failed for student {StudentId}", student.StudentId);
            }
        }
        return updated;
    }

    private async Task<bool> ApplyOneAsync(
        StudentDocument student,
        List<AssessmentDocument> assessments,
        TierRulesetConfigDocument ruleset,
        int systemAdminId,
        string systemAdminEmail)
    {
        var computation = ComputeAll(student, assessments, ruleset);
        var now = DateTime.UtcNow.ToString("o");

        var elaChanged = ApplySubject(student.ElaTier, computation.Ela, computation.RulesetVersion, now, out var elaTierMoved);
        var mathChanged = ApplySubject(student.MathTier, computation.Math, computation.RulesetVersion, now, out var mathTierMoved);

        if (!elaChanged && !mathChanged) return false;

        student.LastUpdated = now;
        await cosmos.UpsertStudentAsync(student);

        // Audit only a material movement in a recommendation. Evidence-only updates — a newly
        // excluded IREAD result, a label the engine could not read, a refreshed explanation —
        // are persisted but deliberately not audited: they report no change to any tier, and
        // logging them would bury the real recommendation history under routine re-imports.
        if (elaTierMoved || mathTierMoved)
        {
            await audit.LogAsync(
                adminId: systemAdminId,
                adminEmail: systemAdminEmail,
                eventType: AuditEventType.TierRecommendation,
                entityType: "Student",
                entityId: student.StudentId,
                details: $"System Tier Recommendation — {student.FullName}: " +
                         $"ELA {DescribeChange(computation.Ela)} | Math {DescribeChange(computation.Math)} | " +
                         $"Ruleset v{computation.RulesetVersion}");
        }

        return true;
    }

    // A subject an administrator has overridden is never overwritten by the system.
    //
    // Returns whether anything needs persisting; `tierMoved` reports the narrower question of
    // whether the recommendation itself changed, which is what gets audited.
    //
    // The distinction matters because a student's evidence can change without their tier, score
    // or data-point count moving at all: an IREAD result arrives for a subject IREAD is excluded
    // from, a source sends a proficiency label the engine cannot map, or a duplicate is
    // superseded. Comparing only the four numeric fields treated those as "no change" and
    // skipped the write — discarding the evidence trail, the pendingReason and the reasoning the
    // engine had just computed. A student whose only result was excluded then showed a bare
    // "Pending" with no explanation, and tier-data-quality (which reports from exactly these
    // persisted fields) could not see the excluded record at all.
    internal static bool ApplySubject(
        SubjectTier target, SubjectTierComputation result, string rulesetVersion, string now, out bool tierMoved)
    {
        tierMoved = false;
        if (TierStatus.IsAdminOverride(target.Status)) return false;

        tierMoved = target.Tier != result.Tier
            || target.Status != result.Status
            || target.Score != result.Score
            || target.DataPoints != result.DataPoints;

        var newEvidence = result.Evidence.Take(24).ToList();
        var explanationChanged = target.PendingReason != result.PendingReason
            || target.Reasoning != result.Reasoning
            || target.RulesetVersion != rulesetVersion
            || !EvidenceMatches(target.Evidence, newEvidence);

        if (!tierMoved && !explanationChanged) return false;

        target.Tier = result.Tier;
        target.Status = result.Status;
        target.Score = result.Score;
        target.DataPoints = result.DataPoints;
        target.PendingReason = result.PendingReason;
        target.Reasoning = result.Reasoning;
        target.RulesetVersion = rulesetVersion;
        target.ComputedAt = now;
        target.Evidence = newEvidence;
        return true;
    }

    /// <summary>Order-sensitive comparison of the persisted evidence trail against a freshly
    /// computed one. ComputeSubject emits counted evidence first and then exclusions, so a stable
    /// input produces a stable order and this does not churn.</summary>
    private static bool EvidenceMatches(List<TierEvidenceRecord> stored, List<TierEvidenceRecord> fresh)
    {
        if (stored.Count != fresh.Count) return false;
        for (var i = 0; i < stored.Count; i++)
        {
            var a = stored[i];
            var b = fresh[i];
            if (a.AssessmentId != b.AssessmentId
                || a.Source != b.Source
                || a.Period != b.Period
                || a.Category != b.Category
                || a.Value != b.Value
                || a.Weight != b.Weight
                || a.Date != b.Date
                || a.Counted != b.Counted
                || a.ExclusionReason != b.ExclusionReason) return false;
        }
        return true;
    }

    private static string DescribeChange(SubjectTierComputation r) =>
        r.Status == "Pending" ? $"Pending ({r.PendingReason})" : $"{r.Tier} (score {r.Score:0.00}, {r.DataPoints} pts)";

    // ── Core calculation (pure, no I/O — testable independently) ─────────────────

    public static StudentTierComputation ComputeAll(StudentDocument student, IReadOnlyList<AssessmentDocument> assessments, TierRulesetConfigDocument ruleset)
    {
        var ela = ComputeSubject("ELA", assessments, ruleset);
        var math = ComputeSubject("Math", assessments, ruleset);
        return new StudentTierComputation(ela, math, ruleset.RulesetVersion);
    }

    public static SubjectTierComputation ComputeSubject(string subject, IReadOnlyList<AssessmentDocument> assessments, TierRulesetConfigDocument ruleset)
    {
        var candidates = assessments.Where(a => MapSubject(a, ruleset) == subject).ToList();

        var evidence = new List<TierEvidenceRecord>();
        var resolved = new List<ResolvedEvidence>();

        foreach (var a in candidates)
        {
            var source = a.UploadType ?? "Unknown";
            var rec = new TierEvidenceRecord
            {
                AssessmentId = a.Id,
                Source = source,
                Category = a.Proficiency,
                Date = a.DateIso ?? a.Date,
            };

            if (ruleset.ExcludedSources.Contains(source, StringComparer.OrdinalIgnoreCase))
            {
                rec.Counted = false;
                rec.ExclusionReason = TierExclusionReason.SourceExcluded;
                evidence.Add(rec);
                continue;
            }

            // Checked before resolution so an assessment the source reported no result for is not
            // filed as a label we failed to read. IXL writes "--" for a diagnostic that was never
            // completed; in the LGS data that is 145 rows across 96 students, and staff need to
            // see "the test was not taken" rather than a parsing complaint.
            if (PerformanceLevelNormalizer.IsNoResultPlaceholder(a.Proficiency))
            {
                rec.Counted = false;
                rec.ExclusionReason = TierExclusionReason.NoResultReported;
                evidence.Add(rec);
                continue;
            }

            if (!PerformanceLevelNormalizer.TryResolve(source, a.Proficiency, ruleset, out var value))
            {
                rec.Counted = false;
                rec.ExclusionReason = TierExclusionReason.UnrecognizedCategory;
                evidence.Add(rec);
                continue;
            }
            rec.Value = value;

            var periodKey = AssessmentNormalization.ResolvePeriodKey(source, a.Period, a.PeriodRaw, a.FileName);
            rec.Period = periodKey;

            double? weight = null;
            if (periodKey is not null && ruleset.EvidenceWeights.TryGetValue(source, out var weights))
            {
                if (weights.TryGetValue(periodKey, out var w)) weight = w;
                else if (weights.TryGetValue("*", out var wildcard)) weight = wildcard;
            }
            weight ??= ruleset.UnknownPeriodWeight;

            if (weight is null)
            {
                rec.Counted = false;
                rec.ExclusionReason = TierExclusionReason.UnknownPeriod;
                evidence.Add(rec);
                continue;
            }
            rec.Weight = weight;

            resolved.Add(new ResolvedEvidence(a, rec, source, periodKey, value, weight.Value));
        }

        // Latest-wins dedup within (source, subject, periodKey). Never compare raw Date strings.
        var winners = resolved
            .GroupBy(r => (r.Source.ToLowerInvariant(), r.PeriodKey?.ToUpperInvariant() ?? ""))
            .Select(g => g
                .OrderByDescending(r => TryParseIso(r.Assessment.DateIso))
                .ThenByDescending(r => TryParseIso(AssessmentNormalization.TryParseFlexibleDate(r.Assessment.Date, r.Source, out _)))
                .ThenByDescending(r => TryParseIso(r.Assessment.UploadedAt))
                .ThenByDescending(r => r.Assessment.Id, StringComparer.Ordinal)
                .ToList())
            .ToList();

        var countedEvidence = new List<TierEvidenceRecord>();
        double weightedSum = 0, weightSum = 0;

        foreach (var group in winners)
        {
            var winner = group[0];
            winner.Record.Counted = true;
            countedEvidence.Add(winner.Record);
            weightedSum += winner.Value * winner.Weight;
            weightSum += winner.Weight;

            foreach (var loser in group.Skip(1))
            {
                loser.Record.Counted = false;
                loser.Record.ExclusionReason = TierExclusionReason.Superseded;
                evidence.Add(loser.Record);
            }
        }

        var allEvidence = countedEvidence.Concat(evidence).ToList();
        var dataPoints = countedEvidence.Count;

        if (dataPoints < ruleset.MinDataPoints)
        {
            string pendingReason;
            if (candidates.Count == 0) pendingReason = "no_assessments";
            else if (dataPoints == 0) pendingReason = "all_evidence_excluded";
            else pendingReason = "insufficient_data_points";

            double? provisionalScore = dataPoints > 0
                ? Math.Round(weightedSum / weightSum, ruleset.ScoreDecimals, MidpointRounding.AwayFromZero)
                : null;

            var reasoning = $"{subject}: Pending / Review — {dataPoints} of {ruleset.MinDataPoints} required data point(s). " +
                            BuildExclusionClause(evidence) + $" Ruleset v{ruleset.RulesetVersion}.";

            return new SubjectTierComputation(subject, "Pending", null, provisionalScore, dataPoints,
                reasoning.Trim(), pendingReason, weightedSum, weightSum, allEvidence);
        }

        var score = Math.Round(weightedSum / weightSum, ruleset.ScoreDecimals, MidpointRounding.AwayFromZero);
        var tier = ruleset.TierThresholds
            .OrderByDescending(t => t.MinScoreInclusive)
            .FirstOrDefault(t => score >= t.MinScoreInclusive)?.Tier
            ?? ruleset.TierThresholds.OrderBy(t => t.MinScoreInclusive).First().Tier;

        var countedDesc = string.Join(", ", countedEvidence.Take(12)
            .Select(e => $"{e.Source} {e.Period} \"{e.Category}\" ({e.Value}×{e.Weight:0.0})"));
        var extra = countedEvidence.Count > 12 ? $" (+{countedEvidence.Count - 12} more)" : "";

        var fullReasoning = $"{subject} {tier} — weighted score {score:0.00} (Σ value×weight {weightedSum:0.00} ÷ Σ weight {weightSum:0.00}) " +
                             $"from {dataPoints} data point(s): {countedDesc}{extra}. " +
                             BuildExclusionClause(evidence) + $" Ruleset v{ruleset.RulesetVersion}.";

        return new SubjectTierComputation(subject, "System Recommended", tier, score, dataPoints,
            fullReasoning.Trim(), null, weightedSum, weightSum, allEvidence);
    }

    /// <summary>Plain-English form of each exclusion reason. Staff reading a tier need to tell an
    /// expected omission (a superseded duplicate) from a data problem they can fix (a period that
    /// could not be identified), which the raw token names do not convey.</summary>
    private static readonly Dictionary<string, string> ExclusionExplanations = new(StringComparer.OrdinalIgnoreCase)
    {
        [TierExclusionReason.UnknownPeriod]        = "the assessment period could not be identified, so no evidence weight could be applied",
        [TierExclusionReason.Superseded]           = "replaced by a more recent result for the same period",
        [TierExclusionReason.SourceExcluded]       = "the source is not part of the weighted calculation",
        [TierExclusionReason.UnrecognizedCategory] = "the proficiency level was not recognised",
        [TierExclusionReason.NoResultReported]     = "the source reported no result, so the assessment was not completed",
        [TierExclusionReason.UnknownSubject]       = "the subject could not be identified as ELA or Math",
    };

    // Grouped by reason rather than listed row by row: a student carrying a dozen duplicate
    // checkpoints would otherwise bury the one record that was dropped for a fixable reason.
    private static string BuildExclusionClause(List<TierEvidenceRecord> excluded)
    {
        if (excluded.Count == 0) return "";

        var groups = excluded
            .GroupBy(e => e.ExclusionReason ?? "unspecified")
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var reason = ExclusionExplanations.TryGetValue(g.Key, out var text)
                    ? text
                    : g.Key.Replace('_', ' ');
                var where = string.Join(", ", g
                    .Select(e => $"{e.Source} {(string.IsNullOrWhiteSpace(e.Period) ? "no period" : e.Period)}")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4));
                return $"{g.Count()} × {reason} ({where})";
            });

        return $"Not included in the calculation — {excluded.Count} record(s): {string.Join("; ", groups)}.";
    }

    private static DateTime TryParseIso(string? iso) =>
        !string.IsNullOrWhiteSpace(iso) && DateTime.TryParse(iso, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : DateTime.MinValue;

    /// <summary>Maps an assessment to ELA/Math/null using the ruleset's source overrides first
    /// (Acadience always -&gt; ELA), then the generic subject text.</summary>
    private static string? MapSubject(AssessmentDocument a, TierRulesetConfigDocument ruleset)
    {
        if (a.UploadType is not null && ruleset.SourceSubjectOverrides.TryGetValue(a.UploadType, out var overrideSubject))
            return overrideSubject;

        var normalized = AssessmentNormalization.NormalizeSubject(a.Subject);
        return normalized switch
        {
            "ELA" => "ELA",
            "Reading" => "ELA",
            "Math" => "Math",
            _ => null,
        };
    }

    private record ResolvedEvidence(AssessmentDocument Assessment, TierEvidenceRecord Record, string Source, string? PeriodKey, int Value, double Weight);
}

// ── Value objects ───────────────────────────────────────────────────────────────

public static class TierStatus
{
    public const string Pending = "Pending";
    public const string SystemRecommended = "System Recommended";

    /// <summary>An administrator has set this subject's tier by hand. The engine never overwrites it.</summary>
    public const string AdminOverride = "Admin Override";

    /// <summary>Previous name for <see cref="AdminOverride"/>. Documents written before the rename
    /// still carry this value, so every read path must accept it — see <see cref="IsAdminOverride"/>.
    /// Never write this value.</summary>
    public const string LegacyFinalized = "Finalized";

    /// <summary>True when a subject's tier was set by a person and must not be recalculated.
    /// Accepts the legacy "Finalized" value so pre-rename overrides stay protected without a
    /// data migration — dropping it would let the engine silently overwrite real admin decisions.</summary>
    public static bool IsAdminOverride(string? status) =>
        string.Equals(status, AdminOverride, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, LegacyFinalized, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Why a single assessment was left out of a subject's weighted score. Surfaced to staff
/// on the student profile, so each value has a plain-English explanation in
/// <c>TierCalculationService.ExclusionExplanations</c>.</summary>
public static class TierExclusionReason
{
    /// <summary>The source is not part of the weighted calculation at all (AC-09: IREAD).</summary>
    public const string SourceExcluded = "source_excluded";

    /// <summary>The proficiency/performance label could not be mapped to a 0-3 value.</summary>
    public const string UnrecognizedCategory = "unrecognized_category";

    /// <summary>The source explicitly reported no result — an IXL "--" for a diagnostic that was
    /// never completed, or an "N/A". A data-collection gap, not a data-quality problem, and the
    /// two need different follow-up.</summary>
    public const string NoResultReported = "no_result_reported";

    /// <summary>No checkpoint or benchmark window could be resolved, so no evidence weight
    /// applies. Usually a fixable data problem rather than an expected omission.</summary>
    public const string UnknownPeriod = "unknown_period";

    /// <summary>The record could not be classified as ELA or Math.</summary>
    public const string UnknownSubject = "unknown_subject";

    /// <summary>A later record exists for the same source, subject and period (spec C-06).</summary>
    public const string Superseded = "superseded";
}

public static class TierPendingReason
{
    public const string NoAssessments = "no_assessments";
    public const string InsufficientDataPoints = "insufficient_data_points";
    public const string AllEvidenceExcluded = "all_evidence_excluded";
}

public record SubjectTierComputation(
    string Subject,
    string Status,
    string? Tier,
    double? Score,
    int DataPoints,
    string Reasoning,
    string? PendingReason,
    double WeightedSum,
    double WeightSum,
    IReadOnlyList<TierEvidenceRecord> Evidence);

public record StudentTierComputation(SubjectTierComputation Ela, SubjectTierComputation Math, string RulesetVersion);
