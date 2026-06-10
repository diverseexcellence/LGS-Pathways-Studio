namespace LgsImpact.Api.Services;

/// <summary>
/// Strips all Tier 1 PII fields before any outbound AI API call.
/// Verified per PRD US-12 — zero PII in Gemini payloads.
/// </summary>
public interface IPiiRedactionService
{
    string BuildRedactedPrompt(string studentId, IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments, string? promptTemplate = null, string? schoolElaAvg = null, string? schoolMathAvg = null);
    Dictionary<string, string> RedactRawFields(Dictionary<string, string> rawFields);
}

public class PiiRedactionService : IPiiRedactionService
{
    // Column headers that may carry Tier 1/2 PII — redacted from stored rawFields
    private static readonly HashSet<string> PiiColumnNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "fullname", "full name", "name", "student name", "first name", "last name", "firstname", "lastname",
        "dob", "date of birth", "birth date", "birthdate",
        "ssn", "social security", "social security number",
        "email", "email address",
        "phone", "phone number", "telephone",
        "address", "street", "zip", "zipcode", "postal code",
        "gender", "sex",
        "ethnicity", "race",
        "ell", "english learner", "ellstatus",
        "sped", "special education", "spedstatus",
        "504", "section 504",
        "guardian", "parent", "parent name", "guardian name",
        "parent email", "guardian email"
    };

    public Dictionary<string, string> RedactRawFields(Dictionary<string, string> rawFields)
    {
        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in rawFields)
        {
            redacted[key] = PiiColumnNames.Contains(key.Trim()) ? "[REDACTED]" : value;
        }
        return redacted;
    }

    public string BuildRedactedPrompt(
        string studentId,
        IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments,
        string? promptTemplate = null,
        string? schoolElaAvg = null,
        string? schoolMathAvg = null)
    {
        // Proficiency-first: omit raw score; include it only when no proficiency label is available
        var lines = assessments.Select(a =>
        {
            var prof = string.IsNullOrWhiteSpace(a.proficiency) ? null : a.proficiency;
            var detail = prof != null
                ? $"Proficiency: {prof}"
                : $"Score: {a.score:F0}";
            return $"- {a.period} | {a.subject} | {a.type} | {detail}";
        });

        var schoolContext = "";
        if (schoolElaAvg != null || schoolMathAvg != null)
        {
            var parts = new List<string>();
            if (schoolElaAvg != null) parts.Add($"school ELA average: {schoolElaAvg}");
            if (schoolMathAvg != null) parts.Add($"school Math average: {schoolMathAvg}");
            schoolContext = $"\nSchool benchmarks for context: {string.Join(", ", parts)}.";
        }

        // Use externalized template when provided (Task 32); fall back to inline default
        if (!string.IsNullOrWhiteSpace(promptTemplate))
        {
            return promptTemplate
                .Replace("{{studentId}}", studentId.ToString())
                .Replace("{{assessmentData}}", string.Join("\n", lines))
                .Replace("{{schoolContext}}", schoolContext);
        }

        // studentId is an internal surrogate — NOT a name, DOB, or any personal identifier
        return $"""
            You are a specialist educational support advisor at LGS, a K-8 school. A student (reference: S-{studentId}) has the following assessment history.
            No personal information is included.{schoolContext}

            Write 3–5 sentences of clear, plain-English narrative for an educator audience:
            1. Lead with the student's current proficiency level in each subject area.
            2. Highlight any meaningful progress or decline across time periods.
            3. Note subject-specific strengths compared to school benchmarks where relevant.
            4. Recommend one or two targeted interventions if the student is below proficiency.
            Do not quote raw numbers as the primary descriptor — translate them into meaningful statements about the student's learning.

            Assessment Records (most recent first):
            {string.Join("\n", lines)}
            """;
    }
}
