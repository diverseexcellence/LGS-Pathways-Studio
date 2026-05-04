namespace LgsImpact.Api.Services;

/// <summary>
/// Strips all Tier 1 PII fields before any outbound AI API call.
/// Verified per PRD US-12 — zero PII in Gemini payloads.
/// </summary>
public interface IPiiRedactionService
{
    string BuildRedactedPrompt(int studentId, IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments);
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
