using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

/// <summary>
/// Dashboard KPI helpers. Isolated so growth math can be unit-tested without Cosmos.
/// </summary>
public static class DashboardMetrics
{
    /// <summary>
    /// Average raw-score change (latest minus earliest) for students who have at least two
    /// scored results from the <b>same</b> instrument, for the given subject. IXL diagnostic
    /// scores (~0–800) and ILEARN scale scores (~5000) are not comparable — mixing them produced
    /// the +5153 "growth" KPI. When a student has two+ of both, ILEARN is preferred (state test).
    /// </summary>
    public static (double? AvgDelta, int StudentCount) SameInstrumentGrowth(
        IEnumerable<AssessmentDocument> assessments, string subject)
    {
        var scored = assessments.Where(a =>
            AssessmentNormalization.NormalizeSubject(a.Subject) == subject
            && !string.IsNullOrWhiteSpace(a.StudentId)
            && a.Score.HasValue
            && !string.IsNullOrWhiteSpace(a.UploadType));

        var deltas = new List<double>();
        foreach (var studentGroup in scored.GroupBy(a => a.StudentId, StringComparer.OrdinalIgnoreCase))
        {
            var series = studentGroup
                .GroupBy(a => a.UploadType, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(a => a.DateIso ?? a.Date ?? a.UploadedAt).ToList())
                .Where(list => list.Count >= 2)
                .ToList();
            if (series.Count == 0) continue;

            var chosen = series.FirstOrDefault(list =>
                             string.Equals(list[0].UploadType, "ILEARN", StringComparison.OrdinalIgnoreCase))
                         ?? series[0];
            deltas.Add(chosen[^1].Score!.Value - chosen[0].Score!.Value);
        }

        if (deltas.Count == 0) return (null, 0);
        return (Math.Round(deltas.Average(), 1), deltas.Count);
    }

    public static (double? AvgDelta, int StudentCount) ElaSameInstrumentGrowth(
        IEnumerable<AssessmentDocument> assessments) => SameInstrumentGrowth(assessments, "ELA");
}
