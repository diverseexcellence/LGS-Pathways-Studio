using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

public interface ITierCalculationService
{
    /// <summary>
    /// Computes ELA and Math tier recommendations independently for the student based on their
    /// current assessments, updates the StudentDocument in Cosmos, and writes an audit entry when
    /// something actually changed. A subject whose status is already "Finalized" is never touched.
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

        var elaChanged = ApplySubject(student.ElaTier, computation.Ela, computation.RulesetVersion, now);
        var mathChanged = ApplySubject(student.MathTier, computation.Math, computation.RulesetVersion, now);

        if (!elaChanged && !mathChanged) return false;

        student.LastUpdated = now;
        await cosmos.UpsertStudentAsync(student);

        await audit.LogAsync(
            adminId: systemAdminId,
            adminEmail: systemAdminEmail,
            eventType: AuditEventType.TierRecommendation,
            entityType: "Student",
            entityId: student.StudentId,
            details: $"System Tier Recommendation — {student.FullName}: " +
                     $"ELA {DescribeChange(computation.Ela)} | Math {DescribeChange(computation.Math)} | " +
                     $"Ruleset v{computation.RulesetVersion}");

        return true;
    }

    // A subject that is already Finalized is never overwritten by the system.
    private static bool ApplySubject(SubjectTier target, SubjectTierComputation result, string rulesetVersion, string now)
    {
        if (target.Status == "Finalized") return false;

        var changed = target.Tier != result.Tier
            || target.Status != result.Status
            || target.Score != result.Score
            || target.DataPoints != result.DataPoints;

        if (!changed) return false;

        target.Tier = result.Tier;
        target.Status = result.Status;
        target.Score = result.Score;
        target.DataPoints = result.DataPoints;
        target.PendingReason = result.PendingReason;
        target.Reasoning = result.Reasoning;
        target.RulesetVersion = rulesetVersion;
        target.ComputedAt = now;
        target.Evidence = result.Evidence.Take(24).ToList();
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
                rec.ExclusionReason = "source_excluded";
                evidence.Add(rec);
                continue;
            }

            if (!PerformanceLevelNormalizer.TryResolve(source, a.Proficiency, ruleset, out var value))
            {
                rec.Counted = false;
                rec.ExclusionReason = "unrecognized_category";
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
                rec.ExclusionReason = "unknown_period";
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
                loser.Record.ExclusionReason = "superseded";
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

    private static string BuildExclusionClause(List<TierEvidenceRecord> excluded)
    {
        if (excluded.Count == 0) return "";
        var items = excluded.Take(5)
            .Select(e => $"{e.Source} {e.Date ?? "n/a"} \"{e.Category ?? "n/a"}\" ({e.ExclusionReason})");
        var extra = excluded.Count > 5 ? $" (+{excluded.Count - 5} more)" : "";
        return $"Excluded: {string.Join("; ", items)}{extra}.";
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
    public const string Finalized = "Finalized";
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
