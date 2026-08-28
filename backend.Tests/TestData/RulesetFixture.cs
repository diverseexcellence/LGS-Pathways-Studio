using LgsImpact.Api.Models;

namespace LgsImpact.Api.Tests.TestData;

public static class RulesetFixture
{
    /// <summary>The spec's default ruleset — CategoryValues/EvidenceWeights/TierThresholds
    /// exactly as documented in LGS_Tier_Recommendation_Logic_Requirements_UpdatedVersion.</summary>
    public static TierRulesetConfigDocument Default() => new();
}

/// <summary>Fluent builder for test AssessmentDocuments, keeping test bodies close to plain
/// English ("Ilearn Math CP2 Approaching Proficiency dated 3/15/2026").</summary>
public class AssessmentBuilder
{
    private readonly AssessmentDocument _doc = new()
    {
        Id = Guid.NewGuid().ToString(),
        StudentId = "s-test",
        FileName = "test.csv",
        UploadedAt = "2026-01-01T00:00:00.0000000Z",
    };

    public static AssessmentBuilder Ilearn(string subject, string period, string? proficiency, string? date = null) =>
        new AssessmentBuilder().Source("ILEARN").Subject(subject).Period(period).Proficiency(proficiency).Date(date);

    public static AssessmentBuilder Ixl(string subject, string period, string? proficiency, string? date = null) =>
        new AssessmentBuilder().Source("IXL").Subject(subject).Period(period).Proficiency(proficiency).Date(date);

    public static AssessmentBuilder Acadience(string period, string? proficiency, string? date = null) =>
        new AssessmentBuilder().Source("Acadience").Subject("Reading").Period(period).Proficiency(proficiency).Date(date);

    public static AssessmentBuilder Iread(string? proficiency, string? date = null) =>
        new AssessmentBuilder().Source("IREAD").Subject("Reading").Proficiency(proficiency).Date(date);

    public AssessmentBuilder Source(string source) { _doc.UploadType = source; return this; }
    public AssessmentBuilder Subject(string subject) { _doc.Subject = subject; return this; }
    public AssessmentBuilder Period(string? period) { _doc.Period = period; _doc.PeriodRaw = period; return this; }
    public AssessmentBuilder Proficiency(string? proficiency) { _doc.Proficiency = proficiency; return this; }
    public AssessmentBuilder Date(string? date)
    {
        if (date is null) return this;
        _doc.Date = date;
        _doc.DateIso = date; // tests pass ISO dates directly unless testing the parser itself
        return this;
    }
    public AssessmentBuilder UploadedAt(string uploadedAt) { _doc.UploadedAt = uploadedAt; return this; }
    public AssessmentBuilder RawField(string key, string value) { _doc.RawFields[key] = value; return this; }
    public AssessmentBuilder Id(string id) { _doc.Id = id; return this; }

    public AssessmentDocument Build() => _doc;
    public static implicit operator AssessmentDocument(AssessmentBuilder b) => b.Build();
}
