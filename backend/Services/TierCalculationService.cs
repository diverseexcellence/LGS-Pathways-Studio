using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

public interface ITierCalculationService
{
    /// <summary>
    /// Computes a recommended tier for the student based on their current assessments.
    /// Updates the StudentDocument in Cosmos if a recommendation can be made.
    /// A system-generated audit entry is written with the reasoning string.
    /// </summary>
    Task ComputeAndApplyAsync(StudentDocument student, int systemAdminId = 0, string systemAdminEmail = "system");
}

public class TierCalculationService(
    ICosmosDbService cosmos,
    IAuditService audit) : ITierCalculationService
{
    // ── Public entry point ────────────────────────────────────────────────────

    public async Task ComputeAndApplyAsync(
        StudentDocument student,
        int systemAdminId = 0,
        string systemAdminEmail = "system")
    {
        var assessments = await cosmos.GetAssessmentsAsync(student.StudentId);
        var result = Compute(student, assessments);

        // Only update if we produced a recommendation (not Pending)
        if (result.TierStatus == TierStatus.SystemRecommended)
        {
            var priorTier       = student.Tier;
            var priorTierStatus = student.TierStatus;

            student.Tier        = result.Tier!;
            student.TierStatus  = TierStatus.SystemRecommended;
            student.LastUpdated = DateTime.UtcNow.ToString("o");
            await cosmos.UpsertStudentAsync(student);

            await audit.LogAsync(
                adminId:    systemAdminId,
                adminEmail: systemAdminEmail,
                eventType:  AuditEventType.Edit,
                entityType: "Student",
                entityId:   student.StudentId,
                details:    $"System tier recommendation: {result.Tier} | {result.Reasoning} | Prior: {priorTier}/{priorTierStatus}");
        }
        else if (result.TierStatus == TierStatus.Pending && student.TierStatus != TierStatus.Finalized)
        {
            // Keep as Pending but record why (insufficient data)
            student.TierStatus  = TierStatus.Pending;
            student.LastUpdated = DateTime.UtcNow.ToString("o");
            await cosmos.UpsertStudentAsync(student);
        }
    }

    // ── Core calculation (pure, no I/O — testable independently) ─────────────

    public static TierResult Compute(StudentDocument student, List<AssessmentDocument> assessments)
    {
        var grade = ParseGrade(student.Grade);

        // Step 1: most recent assessment per normalised subject
        var latestEla  = assessments
            .Where(a => NormalizeSubject(a.Subject) == "ELA")
            .OrderByDescending(a => a.Date)
            .FirstOrDefault();

        var latestMath = assessments
            .Where(a => NormalizeSubject(a.Subject) == "Math")
            .OrderByDescending(a => a.Date)
            .FirstOrDefault();

        // Reading (Acadience / I-Read) — used for K-2 single-subject fallback
        var latestReading = assessments
            .Where(a => NormalizeSubject(a.Subject) == "Reading")
            .OrderByDescending(a => a.Date)
            .FirstOrDefault();

        // Step 2: resolve On/Above for each subject
        bool? elaOnAbove  = ResolveOnAbove(latestEla);
        bool? mathOnAbove = ResolveOnAbove(latestMath);

        // K-2 single-subject rule: if Math missing but Reading present, use Reading as proxy
        if (mathOnAbove is null && grade is >= 0 and <= 2 && latestReading != null)
            mathOnAbove = ResolveOnAbove(latestReading);

        // If ELA also missing but Reading present for K-2, use Reading as ELA proxy too
        if (elaOnAbove is null && grade is >= 0 and <= 2 && latestReading != null)
            elaOnAbove = ResolveOnAbove(latestReading);

        // Step 3: combine
        if (elaOnAbove is null && mathOnAbove is null)
        {
            return new TierResult(
                TierStatus.Pending, null,
                "Pending: no assessments with evaluable proficiency or percentile data.");
        }

        if (elaOnAbove is null || mathOnAbove is null)
        {
            // Single subject available — use it for both signals
            var onAbove = elaOnAbove ?? mathOnAbove!.Value;
            var subjectLabel = elaOnAbove.HasValue
                ? DescribeAssessment(latestEla, "ELA")
                : DescribeAssessment(latestMath ?? latestReading, mathOnAbove.HasValue ? "Math" : "Reading");

            var tier = onAbove ? "Tier 1" : "Tier 3";
            var reasoning = $"Single-subject evaluation ({subjectLabel} → {(onAbove ? "On/Above" : "Below")}). " +
                            "Second subject data unavailable; same signal used for both.";
            return new TierResult(TierStatus.SystemRecommended, tier, reasoning);
        }

        // Both signals available
        var elaTier  = elaOnAbove.Value;
        var mathTier = mathOnAbove.Value;

        string resultTier;
        string tierReasoning;

        if (elaTier && mathTier)
        {
            resultTier    = "Tier 1";
            tierReasoning = $"Based on {DescribeAssessment(latestEla, "ELA")} → On/Above and " +
                            $"{DescribeAssessment(latestMath, "Math")} → On/Above.";
        }
        else if (!elaTier && !mathTier)
        {
            resultTier    = "Tier 3";
            tierReasoning = $"Based on {DescribeAssessment(latestEla, "ELA")} → Below and " +
                            $"{DescribeAssessment(latestMath, "Math")} → Below.";
        }
        else
        {
            resultTier    = "Tier 2";
            var elaLabel  = $"{DescribeAssessment(latestEla, "ELA")} → {(elaTier ? "On/Above" : "Below")}";
            var mathLabel = $"{DescribeAssessment(latestMath, "Math")} → {(mathTier ? "On/Above" : "Below")}";
            tierReasoning = $"Based on {elaLabel} and {mathLabel}.";
        }

        return new TierResult(TierStatus.SystemRecommended, resultTier, tierReasoning);
    }

    // ── Proficiency resolution (BRD section 10.1) ─────────────────────────────

    private static bool? ResolveOnAbove(AssessmentDocument? assessment)
    {
        if (assessment is null) return null;

        var p = assessment.Proficiency?.Trim();

        if (!string.IsNullOrWhiteSpace(p))
        {
            // Explicit tier strings
            if (p.Equals("Tier 1", StringComparison.OrdinalIgnoreCase)) return true;
            if (p.Equals("Tier 2", StringComparison.OrdinalIgnoreCase)) return false;
            if (p.Equals("Tier 3", StringComparison.OrdinalIgnoreCase)) return false;

            // Below signals (check before "above" to avoid substring collision on "far below above")
            if (ContainsAny(p, "far below", "below", "approaching")) return false;

            // On/Above signals
            if (ContainsAny(p, "above", "on grade", "at grade", "at proficiency",
                               "proficient", "meets", "exceeds", "mid above", "early on")) return true;
        }

        // Percentile fallback (40th-percentile cutoff — confirmed 2026-06-12)
        var percentileValue = ExtractPercentile(assessment);
        if (percentileValue.HasValue)
            return percentileValue.Value >= 40;

        return null; // unresolvable
    }

    private static double? ExtractPercentile(AssessmentDocument assessment)
    {
        // Check the typed Score field if the subject implies a percentile context
        foreach (var kv in assessment.RawFields)
        {
            if (kv.Key.Contains("percentile", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(kv.Value, System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var pct))
                return pct;
        }
        return null;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string NormalizeSubject(string? subject)
    {
        if (subject is null) return "Unknown";
        if (subject.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (subject.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        if (subject.Contains("Reading", StringComparison.OrdinalIgnoreCase)) return "Reading";
        return subject;
    }

    private static string DescribeAssessment(AssessmentDocument? a, string subjectLabel)
    {
        if (a is null) return $"{subjectLabel} (no data)";
        var proficiency = string.IsNullOrWhiteSpace(a.Proficiency) ? "no label" : a.Proficiency;
        return $"{subjectLabel} ({a.UploadType}: {proficiency})";
    }

    private static int? ParseGrade(string? grade)
    {
        if (grade is null) return null;
        var g = grade.Trim().ToUpperInvariant();
        if (g == "K" || g == "KG" || g == "KINDERGARTEN") return 0;
        if (int.TryParse(g, out var n)) return n;
        return null;
    }

    private static bool ContainsAny(string source, params string[] terms)
        => terms.Any(t => source.Contains(t, StringComparison.OrdinalIgnoreCase));
}

// ── Value objects ─────────────────────────────────────────────────────────────

public static class TierStatus
{
    public const string Pending             = "Pending";
    public const string SystemRecommended   = "System Recommended";
    public const string Finalized           = "Finalized";
}

public record TierResult(string TierStatus, string? Tier, string Reasoning);
