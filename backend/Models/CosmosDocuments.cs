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

    [JsonProperty("tier")]
    public string Tier { get; set; } = "Pending";

    [JsonProperty("tierStatus")]
    public string TierStatus { get; set; } = "Pending";

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
