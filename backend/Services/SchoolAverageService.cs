using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

/// <summary>
/// Reads and refreshes cached school-wide ELA/Math averages in the Cosmos config container.
/// Averages are updated on each assessment write (O(1) read on summary generation — BRD NF-PERF-3).
/// </summary>
public interface ISchoolAverageService
{
    Task<SchoolAverageDocument?> GetAsync();
    Task RefreshAsync();
}

public class SchoolAverageService(ICosmosDbService cosmos) : ISchoolAverageService
{
    public Task<SchoolAverageDocument?> GetAsync() => cosmos.GetSchoolAveragesAsync();

    public async Task RefreshAsync()
    {
        // Fetch all assessments that have a subject we recognise
        var elaScores  = new List<double>();
        var mathScores = new List<double>();
        var elaProficiencies  = new List<string>();
        var mathProficiencies = new List<string>();

        // Pull assessments for all students via the service (cross-partition, in-memory aggregation)
        var allAssessments = await cosmos.GetAllAssessmentsAsync();

        foreach (var a in allAssessments)
        {
            var subject = (a.Subject ?? "").ToLowerInvariant();
            var isEla  = subject.Contains("ela") || subject.Contains("reading") || subject.Contains("english");
            var isMath = subject.Contains("math");

            if (isEla)
            {
                if (a.Score.HasValue) elaScores.Add(a.Score.Value);
                if (!string.IsNullOrWhiteSpace(a.Proficiency)) elaProficiencies.Add(a.Proficiency!);
            }
            else if (isMath)
            {
                if (a.Score.HasValue) mathScores.Add(a.Score.Value);
                if (!string.IsNullOrWhiteSpace(a.Proficiency)) mathProficiencies.Add(a.Proficiency!);
            }
        }

        var doc = new SchoolAverageDocument
        {
            Id            = "school-averages",
            PartitionKey  = "school-averages",
            ElaAvgScore   = elaScores.Count > 0 ? Math.Round(elaScores.Average(), 1) : null,
            MathAvgScore  = mathScores.Count > 0 ? Math.Round(mathScores.Average(), 1) : null,
            ElaAvgProficiency  = MostCommonProficiency(elaProficiencies),
            MathAvgProficiency = MostCommonProficiency(mathProficiencies),
            LastUpdated   = DateTime.UtcNow.ToString("o")
        };

        await cosmos.UpsertSchoolAveragesAsync(doc);
    }

    private static string? MostCommonProficiency(List<string> labels)
    {
        if (labels.Count == 0) return null;
        return labels
            .GroupBy(l => l, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .First().Key;
    }
}
