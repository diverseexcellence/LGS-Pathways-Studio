namespace LgsImpact.Api.Services;

/// <summary>
/// Strips all Tier 1 PII fields before any outbound AI API call.
/// Verified per PRD US-12 — zero PII in Gemini payloads.
/// </summary>
public interface IPiiRedactionService
{
    string BuildRedactedPrompt(string studentId, IEnumerable<(string subject, string type, double score, string? proficiency, string period)> assessments, string? grade = null, string? promptTemplate = null, string? schoolElaAvg = null, string? schoolMathAvg = null);
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
        string? grade = null,
        string? promptTemplate = null,
        string? schoolElaAvg = null,
        string? schoolMathAvg = null)
    {
        var assessmentList = assessments.ToList();

        // Include both proficiency label and score for each record
        var lines = assessmentList.Select(a =>
        {
            var prof = string.IsNullOrWhiteSpace(a.proficiency) ? null : a.proficiency;
            var detail = prof != null
                ? $"Proficiency: {prof}, Score: {a.score:F0}"
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
                .Replace("{{grade}}", grade ?? "Unknown")
                .Replace("{{assessmentData}}", string.Join("\n", lines))
                .Replace("{{schoolContext}}", schoolContext);
        }

        var gradeLabel = string.IsNullOrWhiteSpace(grade) ? "" : $" – Grade {grade}";
        var refId = studentId.ToUpper().Replace("S-S-", "S-");

        // studentId is an internal surrogate — NOT a name, DOB, or any personal identifier
        return $"""
            You are a specialist educational support advisor at LGS, a K-8 school.
            Produce a structured academic progress summary for student reference {refId}{gradeLabel}.
            No personal information is included.{schoolContext}

            Output your response in the following Markdown format exactly:

            **Student Academic Progress Summary{gradeLabel}**

            [One sentence overview of the student's overall performance pattern across subjects.]

            Scores are included for reference, but proficiency status is used as the main indicator of performance.

            ### ELA Performance
            [One sentence describing the overall ELA trend.]

            [Bullet list: one bullet per ELA assessment record in format: **[assessment name]** was marked [proficiency] with a score of [score].]

            [One paragraph summarising the ELA trend and what the latest result means.]

            ### Math Performance
            [One sentence describing the overall Math trend.]

            [Bullet list: one bullet per Math assessment record in format: **[assessment name]** was marked [proficiency] with a score of [score].]

            [One paragraph summarising the Math trend and what the latest result means.]

            ### Reading Performance
            [One sentence describing the overall Reading trend (Acadience / I-Read data).]

            [Bullet list: one bullet per Reading assessment record in format: **[assessment name]** was marked [proficiency] with a score of [score].]

            [One paragraph summarising the Reading trend and what it means for early literacy.]

            [One concluding sentence comparing performance across subjects.]

            ### Suggestions
            - [Suggestion 1 — specific to lowest-performing subject]
            - [Suggestion 2 — how the low subject should influence the tier decision]
            - [Suggestion 3 — monitoring recommendation for stronger subject]
            - Use the system-suggested tier as a starting point, with the final tier reviewed and finalized by the Administrator.

            Rules:
            - Group assessments by subject exactly as labelled in the records: ELA → ELA Performance, Math → Math Performance, Reading → Reading Performance (Acadience/I-Read). Do NOT merge Reading into ELA.
            - If a subject has no assessments, omit that section entirely.
            - Do not invent data. Only reference assessments provided below.
            - Use plain language suitable for an educator audience.
            - Never include the student's name, date of birth, or any personal identifier.

            Assessment Records (most recent first):
            {string.Join("\n", lines)}
            """;
    }
}
