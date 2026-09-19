using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

/// <summary>
/// One hand-entered assessment result. Mirrors the fields the row processor reads out of a CSV so a
/// manually added record is indistinguishable to the tier engine from an imported one.
/// </summary>
public record ManualAssessmentInput(
    string UploadType,
    string? Subject = null,
    string? Period = null,
    double? Score = null,
    string? Proficiency = null,
    string? Date = null);

/// <summary>
/// Builds <see cref="AssessmentDocument"/>s from hand-entered input using the same
/// <see cref="AssessmentNormalization"/> calls the CSV row processor uses. A manual record that
/// skipped this normalization would be stored with a raw period ("Checkpoint 2") and a raw subject
/// ("Mathematics"), which the tier engine cannot weight or classify — the record would land on the
/// profile and then be silently excluded from the score, which is exactly the failure mode manual
/// entry exists to avoid.
/// </summary>
public static class ManualAssessmentFactory
{
    /// <summary>Stored as the record's FileName so the profile, the evidence trail and the export
    /// can all say where the data came from. Not a real file — no upload can produce this name,
    /// since uploads only accept .csv/.xlsx.</summary>
    public const string SourceLabel = "Manual entry";

    /// <summary>Returns a user-facing validation message, or null when the input is usable.</summary>
    public static string? Validate(ManualAssessmentInput input, IReadOnlyCollection<string> supportedSources)
    {
        if (string.IsNullOrWhiteSpace(input.UploadType))
            return "An assessment source is required.";

        if (!supportedSources.Contains(input.UploadType, StringComparer.OrdinalIgnoreCase))
            return $"Unsupported assessment source \"{input.UploadType}\". Choose one of: " +
                   $"{string.Join(", ", supportedSources.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}.";

        // A record with neither a performance level nor a score carries no signal: the tier engine
        // would record it as unrecognized_category and nothing else would display it either.
        if (string.IsNullOrWhiteSpace(input.Proficiency) && input.Score is null)
            return "Enter a performance level, a score, or both — a record with neither cannot be used.";

        if (!string.IsNullOrWhiteSpace(input.Date) &&
            AssessmentNormalization.TryParseFlexibleDate(input.Date, input.UploadType, out _) is null)
            return $"Could not read the date \"{input.Date}\". Use YYYY-MM-DD.";

        return null;
    }

    public static AssessmentDocument Build(string studentId, ManualAssessmentInput input, string enteredBy)
    {
        var doc = new AssessmentDocument
        {
            Id         = Guid.NewGuid().ToString(),
            StudentId  = studentId,
            FileName   = SourceLabel,
            UploadedAt = DateTime.UtcNow.ToString("o"),
        };
        Apply(doc, input, enteredBy);
        return doc;
    }

    /// <summary>Applies normalized input onto a new or existing record. Used by both create and
    /// edit so an edited record is normalized the same way a new one is.</summary>
    public static void Apply(AssessmentDocument target, ManualAssessmentInput input, string enteredBy)
    {
        var uploadType = input.UploadType.Trim();
        var periodRaw = Blank(input.Period);
        var rawProficiency = Blank(input.Proficiency);

        var subject = Blank(input.Subject) ?? AssessmentNormalization.DetectSubject(uploadType, "", null);

        target.UploadType  = uploadType;
        target.Subject     = AssessmentNormalization.NormalizeSubject(subject);
        target.PeriodRaw   = periodRaw;
        target.Period      = AssessmentNormalization.NormalizePeriod(uploadType, periodRaw, "");
        target.Score       = input.Score;
        target.Proficiency = uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase)
            ? AssessmentNormalization.NormalizeIReadProficiency(rawProficiency)
            : rawProficiency;
        target.Date        = Blank(input.Date);
        target.DateIso     = AssessmentNormalization.TryParseFlexibleDate(target.Date, uploadType, out var ambiguous);
        target.DateAmbiguous = ambiguous;

        // Provenance in place of a source file's columns — the assessment details modal reads
        // rawFields, so without this a manual record shows an empty source section.
        target.RawFields = new Dictionary<string, string>
        {
            ["Entry Method"] = SourceLabel,
            ["Entered By"]   = enteredBy,
        };
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
