namespace LgsImpact.Api.Services;

/// <summary>
/// Strips all Tier 1 PII fields before any outbound AI API call.
/// Verified per PRD US-12 — zero PII in Gemini payloads.
/// </summary>
public interface IPiiRedactionService
{
    string BuildRedactedPrompt(int studentId, IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments);
}

public class PiiRedactionService : IPiiRedactionService
{
    public string BuildRedactedPrompt(int studentId, IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments)
    {
        var lines = assessments.Select(a =>
            $"- {a.period} | {a.type} | {a.subject} | Score: {a.score} | Proficiency: {a.proficiency ?? "N/A"}");

        // studentId is an internal surrogate — NOT a name, DOB, or any personal identifier
        return $"""
            You are an educational data analyst. A student (internal reference: S-{studentId}) has the following assessment records.
            No personal information is included. Summarise academic progress, identify strengths and areas for growth,
            and recommend intervention strategies if needed. Be concise (3–5 sentences).

            Assessment Data:
            {string.Join("\n", lines)}
            """;
    }
}
