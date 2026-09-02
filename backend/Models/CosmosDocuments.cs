using LgsImpact.Api.Services;
using Newtonsoft.Json;

namespace LgsImpact.Api.Models;

public class StudentDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("studentId")]
    public string StudentId { get; set; } = default!;

    [JsonProperty("fullName")]
    public string FullName { get; set; } = default!;

    [JsonProperty("dob")]
    public string? Dob { get; set; }

    [JsonProperty("stn")]
    public string? Stn { get; set; }

    [JsonProperty("classGroup")]
    public string ClassGroup { get; set; } = "";

    [JsonProperty("grade")]
    public string? Grade { get; set; }

    [JsonProperty("gender")]
    public string? Gender { get; set; }

    [JsonProperty("ethnicity")]
    public string? Ethnicity { get; set; }

    [JsonProperty("ellStatus")]
    public string? EllStatus { get; set; }

    [JsonProperty("spedStatus")]
    public string? SpedStatus { get; set; }

    [JsonProperty("section504")]
    public string? Section504 { get; set; }

    [JsonProperty("homeRoom")]
    public string? HomeRoom { get; set; }

    /// <summary>ELA subject-level tier recommendation. Independent of Math — see TR-011 (no combined tier).</summary>
    [JsonProperty("elaTier")]
    public SubjectTier ElaTier { get; set; } = new();

    /// <summary>Math subject-level tier recommendation. Independent of ELA — see TR-011 (no combined tier).</summary>
    [JsonProperty("mathTier")]
    public SubjectTier MathTier { get; set; } = new();

    [JsonIgnore]
    public bool AllSubjectsOverridden =>
        TierStatus.IsAdminOverride(ElaTier.Status) && TierStatus.IsAdminOverride(MathTier.Status);

    [JsonIgnore]
    public bool AnySubjectOverridden =>
        TierStatus.IsAdminOverride(ElaTier.Status) || TierStatus.IsAdminOverride(MathTier.Status);

    [JsonProperty("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonProperty("enrolDate")]
    public string EnrolDate { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("lastUpdated")]
    public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("localId")]
    public string? LocalId { get; set; }

    [JsonProperty("entryDate")]
    public string? EntryDate { get; set; }

    [JsonProperty("exitDate")]
    public string? ExitDate { get; set; }

    [JsonProperty("lunchStatus")]
    public string? LunchStatus { get; set; }

    [JsonProperty("zipCode")]
    public string? ZipCode { get; set; }

    [JsonProperty("sourceFile")]
    public string? SourceFile { get; set; }
}

public class AssessmentDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("studentId")]
    public string StudentId { get; set; } = default!;

    [JsonProperty("uploadType")]
    public string UploadType { get; set; } = default!;

    [JsonProperty("fileName")]
    public string FileName { get; set; } = default!;

    [JsonProperty("uploadedAt")]
    public string UploadedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("subject")]
    public string? Subject { get; set; }

    [JsonProperty("score")]
    public double? Score { get; set; }

    [JsonProperty("proficiency")]
    public string? Proficiency { get; set; }

    [JsonProperty("period")]
    public string? Period { get; set; }

    [JsonProperty("date")]
    public string? Date { get; set; }

    [JsonProperty("rawFields")]
    public Dictionary<string, string> RawFields { get; set; } = new();

    /// <summary>Original, un-normalized period column/filename value. Never overwritten — lets a
    /// bad normalization be corrected without losing the source data.</summary>
    [JsonProperty("periodRaw")]
    public string? PeriodRaw { get; set; }

    /// <summary>Parsed "yyyy-MM-dd" form of <see cref="Date"/>, resolved with per-source format
    /// knowledge. Used for chronological ordering — never sort on the raw <see cref="Date"/> string.</summary>
    [JsonProperty("dateIso")]
    public string? DateIso { get; set; }

    /// <summary>True when the day/month order in <see cref="Date"/> was ambiguous (both segments
    /// &lt;= 12) and a best-guess format was assumed.</summary>
    [JsonProperty("dateAmbiguous")]
    public bool DateAmbiguous { get; set; }
}

/// <summary>Subject-level tier recommendation (ELA or Math). Two of these live on a
/// <see cref="StudentDocument"/> — there is no combined overall tier (TR-011).</summary>
public class SubjectTier
{
    /// <summary>"Tier 1" | "Tier 2" | "Tier 3" | null when Pending.</summary>
    [JsonProperty("tier")]
    public string? Tier { get; set; }

    /// <summary>Workflow state: "Pending" | "System Recommended" | "Admin Override".
    /// Documents written before the rename may still hold the legacy "Finalized" value —
    /// compare with <see cref="TierStatus.IsAdminOverride"/>, never with a string literal.</summary>
    [JsonProperty("status")]
    public string Status { get; set; } = "Pending";

    /// <summary>Weighted performance score, 0.00-3.00. Populated even while Pending once at least
    /// one data point counts, so the profile can show a provisional number.</summary>
    [JsonProperty("score")]
    public double? Score { get; set; }

    /// <summary>Count of evidence records that counted toward the score.</summary>
    [JsonProperty("dataPoints")]
    public int DataPoints { get; set; }

    /// <summary>"no_assessments" | "insufficient_data_points" | "all_evidence_excluded" — set only while Pending.</summary>
    [JsonProperty("pendingReason")]
    public string? PendingReason { get; set; }

    /// <summary>Deterministic, human-readable explanation of the calculation, versioned by ruleset.</summary>
    [JsonProperty("reasoning")]
    public string? Reasoning { get; set; }

    [JsonProperty("rulesetVersion")]
    public string? RulesetVersion { get; set; }

    [JsonProperty("computedAt")]
    public string? ComputedAt { get; set; }

    [JsonProperty("overriddenBy")]
    public string? OverriddenBy { get; set; }

    [JsonProperty("overriddenAt")]
    public string? OverriddenAt { get; set; }

    /// <summary>Full evidence trail: every candidate record considered, whether it counted, and why
    /// not when it didn't. Capped to a reasonable size before persisting.</summary>
    [JsonProperty("evidence", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<TierEvidenceRecord> Evidence { get; set; } = new();
}

public class TierEvidenceRecord
{
    [JsonProperty("assessmentId")]
    public string AssessmentId { get; set; } = "";

    /// <summary>ILEARN | IXL | Acadience | IREAD (the assessment's UploadType).</summary>
    [JsonProperty("source")]
    public string Source { get; set; } = "";

    /// <summary>CP1 | CP2 | CP3 | SPRING | BOY | MOY | EOY | null when unresolved.</summary>
    [JsonProperty("period")]
    public string? Period { get; set; }

    /// <summary>Raw performance-level label as stored on the assessment.</summary>
    [JsonProperty("category")]
    public string? Category { get; set; }

    [JsonProperty("value")]
    public int? Value { get; set; }

    [JsonProperty("weight")]
    public double? Weight { get; set; }

    [JsonProperty("date")]
    public string? Date { get; set; }

    [JsonProperty("counted")]
    public bool Counted { get; set; }

    /// <summary>"source_excluded" | "unrecognized_category" | "unknown_period" | "unknown_subject" | "superseded".</summary>
    [JsonProperty("exclusionReason")]
    public string? ExclusionReason { get; set; }
}

public class AiSummaryDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("studentId")]
    public string StudentId { get; set; } = default!;

    [JsonProperty("summaryText")]
    public string SummaryText { get; set; } = default!;

    [JsonProperty("generatedAt")]
    public string GeneratedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("modelUsed")]
    public string ModelUsed { get; set; } = "llama3.2";
}

public class UploadLogDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("uploadedBy")]
    public string UploadedBy { get; set; } = default!;

    [JsonProperty("fileName")]
    public string FileName { get; set; } = default!;

    [JsonProperty("uploadType")]
    public string UploadType { get; set; } = default!;

    [JsonProperty("uploadedAt")]
    public string UploadedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("recordCount")]
    public int RecordCount { get; set; }

    [JsonProperty("skippedCount")]
    public int SkippedCount { get; set; }

    [JsonProperty("errors")]
    public List<string> Errors { get; set; } = new();

    [JsonProperty("blobUrl")]
    public string? BlobUrl { get; set; }

    [JsonProperty("contentHash")]
    public string? ContentHash { get; set; }
}

public class ExportLogDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("exportedBy")]
    public string ExportedBy { get; set; } = default!;

    [JsonProperty("fileName")]
    public string FileName { get; set; } = default!;

    [JsonProperty("exportedAt")]
    public string ExportedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("recordCount")]
    public int RecordCount { get; set; }

    [JsonProperty("blobUrl")]
    public string? BlobUrl { get; set; }
}

public class AuditLogDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("adminId")]
    public int AdminId { get; set; }

    [JsonProperty("adminEmail")]
    public string AdminEmail { get; set; } = default!;

    [JsonProperty("eventType")]
    public string EventType { get; set; } = default!;

    [JsonProperty("entityType")]
    public string? EntityType { get; set; }

    [JsonProperty("entityId")]
    public string? EntityId { get; set; }

    [JsonProperty("details")]
    public string? Details { get; set; }

    [JsonProperty("timestamp")]
    public string Timestamp { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("ipAddress")]
    public string? IpAddress { get; set; }

    [JsonProperty("userAgent")]
    public string? UserAgent { get; set; }
}

public class SchoolAverageDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "school-averages";

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "school-averages";

    [JsonProperty("elaAvgProficiency")]
    public string? ElaAvgProficiency { get; set; }

    [JsonProperty("mathAvgProficiency")]
    public string? MathAvgProficiency { get; set; }

    [JsonProperty("elaAvgScore")]
    public double? ElaAvgScore { get; set; }

    [JsonProperty("mathAvgScore")]
    public double? MathAvgScore { get; set; }

    [JsonProperty("lastUpdated")]
    public string LastUpdated { get; set; } = DateTime.UtcNow.ToString("o");
}

public class PromptConfigDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "ai-summary-prompt";

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "prompts";

    /// <summary>
    /// Prompt template. Placeholders: {{studentId}}, {{assessmentData}}, {{schoolContext}}.
    /// When null the inline default in PiiRedactionService is used.
    /// </summary>
    [JsonProperty("template")]
    public string? Template { get; set; }

    [JsonProperty("version")]
    public string Version { get; set; } = "1.0";

    [JsonProperty("updatedAt")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("updatedBy")]
    public string? UpdatedBy { get; set; }
}

public class TargetGoalDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "target-goal";

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "config";

    [JsonProperty("goalPct")]
    public int GoalPct { get; set; } = 85;

    [JsonProperty("updatedAt")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("updatedBy")]
    public string? UpdatedBy { get; set; }
}

public record TierThreshold(
    [property: JsonProperty("tier")] string Tier,
    [property: JsonProperty("minScoreInclusive")] double MinScoreInclusive);

/// <summary>
/// Per-subject weighted tier calculation ruleset (LGS Tier Recommendation Logic Requirements,
/// updated version). Every weight, category mapping, and threshold used by
/// <c>TierCalculationService</c> lives here so LGS can adjust the model without a redeploy.
/// </summary>
public class TierRulesetConfigDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = "tier-ruleset";

    [JsonProperty("partitionKey")]
    public string PartitionKey { get; set; } = "config";

    [JsonProperty("rulesetVersion")]
    public string RulesetVersion { get; set; } = "2.0";

    [JsonProperty("effectiveDate")]
    public string EffectiveDate { get; set; } = "2026-08-27";

    [JsonProperty("description")]
    public string Description { get; set; } =
        "Weighted per-subject tiering (ELA and Math calculated independently; no combined overall " +
        "tier). Score = Σ(performance value 0-3 × evidence weight) ÷ Σ(available evidence weights). " +
        "Missing evidence is excluded from both sums, never coerced to 0. Minimum 2 counted data " +
        "points per subject for automatic tiering, else Pending / Review.";

    // ── DEPRECATED — kept only so a v1.0 config document round-trips; unused by the v2 engine. ──
    [JsonProperty("percentileCutoff")]
    public int PercentileCutoff { get; set; } = 40;

    /// <summary>source (UploadType) -&gt; canonicalised category label -&gt; normalized value 0-3.</summary>
    [JsonProperty("categoryValues", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, Dictionary<string, int>> CategoryValues { get; set; } = DefaultCategoryValues();

    /// <summary>Category labels recognised for every source, checked after the source-specific map.</summary>
    [JsonProperty("sharedCategoryValues", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, int> SharedCategoryValues { get; set; } = DefaultSharedCategoryValues();

    /// <summary>source -&gt; period key ("CP1", "BOY", "*", ...) -&gt; evidence weight.</summary>
    [JsonProperty("evidenceWeights", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, Dictionary<string, double>> EvidenceWeights { get; set; } = DefaultEvidenceWeights();

    /// <summary>Sources excluded from the weighted calculation entirely (AC-09: IREAD). Evidence
    /// from an excluded source is still recorded on the profile as display-only.</summary>
    [JsonProperty("excludedSources", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<string> ExcludedSources { get; set; } = new() { "IREAD" };

    /// <summary>source (UploadType) -&gt; subject it always contributes to, overriding whatever the
    /// stored Subject column says (Acadience always contributes to ELA/Reading — §3, §5.3).</summary>
    [JsonProperty("sourceSubjectOverrides", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public Dictionary<string, string> SourceSubjectOverrides { get; set; } = new() { ["Acadience"] = "ELA" };

    /// <summary>Checked in descending MinScoreInclusive order; first match wins.</summary>
    [JsonProperty("tierThresholds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<TierThreshold> TierThresholds { get; set; } = new()
    {
        new("Tier 1", 2.00),
        new("Tier 2", 1.00),
        new("Tier 3", 0.00),
    };

    /// <summary>Minimum counted data points per subject for automatic tiering (TR-009, AC-10).</summary>
    [JsonProperty("minDataPoints")]
    public int MinDataPoints { get; set; } = 2;

    [JsonProperty("scoreDecimals")]
    public int ScoreDecimals { get; set; } = 2;

    /// <summary>Weight applied when a record's period cannot be resolved. Null (default) means such
    /// evidence is excluded — it is never silently given a weight of 1.0.</summary>
    [JsonProperty("unknownPeriodWeight")]
    public double? UnknownPeriodWeight { get; set; } = null;

    /// <summary>TR-003 removes the percentile fallback used by the old engine. Kept as an opt-in
    /// escape hatch; default false.</summary>
    [JsonProperty("percentileFallbackEnabled")]
    public bool PercentileFallbackEnabled { get; set; } = false;

    /// <summary>When true, an IXL record with no explicit BOY/MOY/EOY token falls back to a
    /// month-of-test-date window (§4b). Default off pending LGS confirmation (C-04).</summary>
    [JsonProperty("ixlPeriodFromDateFallback")]
    public bool IxlPeriodFromDateFallback { get; set; } = false;

    [JsonProperty("updatedAt")]
    public string UpdatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("updatedBy")]
    public string? UpdatedBy { get; set; }

    public static Dictionary<string, Dictionary<string, int>> DefaultCategoryValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ILEARN"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["below proficiency"] = 0,
            ["approaching proficiency"] = 1,
            ["at proficiency"] = 2,
            ["above proficiency"] = 3,
        },
        ["IXL"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["far below grade"] = 0,
            ["far below grade level"] = 0,
            ["below grade"] = 1,
            ["below grade level"] = 1,
            ["on grade"] = 2,
            ["on grade level"] = 2,
            ["above grade"] = 3,
            ["above grade level"] = 3,
        },
        ["Acadience"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["well below benchmark"] = 0,
            ["below benchmark"] = 1,
            ["at benchmark"] = 2,
            ["above benchmark"] = 3,
        },
    };

    public static Dictionary<string, int> DefaultSharedCategoryValues() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["below"] = 0,
        ["far below"] = 0,
        ["did not meet"] = 0,
        ["approaching"] = 1,
        ["at"] = 2,
        ["on"] = 2,
        ["meets"] = 2,
        ["above"] = 3,
        ["exceeds"] = 3,
    };

    public static Dictionary<string, Dictionary<string, double>> DefaultEvidenceWeights() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["ILEARN"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["CP1"] = 1.0,
            ["CP2"] = 1.5,
            ["CP3"] = 2.0,
            ["SPRING"] = 2.5,
        },
        ["IXL"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BOY"] = 1.0,
            ["MOY"] = 1.5,
            ["EOY"] = 2.0,
        },
        ["Acadience"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["BOY"] = 1.0,
            ["MOY"] = 1.0,
            ["EOY"] = 1.0,
            ["*"] = 1.0,
        },
    };
}

public class CollaborationNoteDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("studentId")]
    public string StudentId { get; set; } = default!;

    [JsonProperty("text")]
    public string Text { get; set; } = default!;

    [JsonProperty("createdAt")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");

    [JsonProperty("createdBy")]
    public string CreatedBy { get; set; } = default!;

    [JsonProperty("isDeleted")]
    public bool IsDeleted { get; set; } = false;

    [JsonProperty("deletedAt")]
    public string? DeletedAt { get; set; }

    [JsonProperty("deletedBy")]
    public string? DeletedBy { get; set; }
}

public class AdminDocument
{
    [JsonProperty("id")]
    public string Id { get; set; } = default!;

    [JsonProperty("adminId")]
    public int AdminId { get; set; }

    [JsonProperty("email")]
    public string Email { get; set; } = default!;

    [JsonProperty("passwordHash")]
    public string PasswordHash { get; set; } = default!;

    [JsonProperty("name")]
    public string Name { get; set; } = default!;

    [JsonProperty("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonProperty("isSuperAdmin")]
    public bool IsSuperAdmin { get; set; } = false;

    [JsonProperty("lastLogin")]
    public string? LastLogin { get; set; }

    [JsonProperty("createdAt")]
    public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
}
