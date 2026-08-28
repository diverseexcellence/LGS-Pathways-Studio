using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class AiController(
    ICosmosDbService cosmos,
    ILlmProvider llm,
    IPiiRedactionService piiRedaction,
    IAuditService audit,
    ISchoolAverageService schoolAverages) : ControllerBase
{
    private int CurrentAdminId => int.Parse(User.FindFirstValue("adminId") ?? "0");
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";

    [HttpGet("summary/{studentId}")]
    public async Task<IActionResult> GetSummary(string studentId)
    {
        var summary = await cosmos.GetLatestSummaryAsync(studentId);
        return Ok(summary);
    }

    [HttpPost("summary/{studentId}")]
    public async Task<IActionResult> GenerateSummary(string studentId, CancellationToken ct)
    {
        var student = await cosmos.GetStudentAsync(studentId);
        if (student is null || !student.IsActive) return NotFound();

        var assessments = await cosmos.GetAssessmentsAsync(studentId);
        if (assessments.Count == 0)
            return BadRequest(new { message = "No assessments found. Upload assessment data first." });

        // Deduplicate: one record per (UploadType, Subject, Period) — most recent wins
        var dedupedAssessments = assessments
            .OrderByDescending(a => a.Date ?? a.UploadedAt)
            .GroupBy(a => $"{a.UploadType}|{ResolveSubject(a.Subject, a.UploadType)}|{a.Period ?? a.Date ?? ""}")
            .Select(g => g.First())
            .Take(20)
            .ToList();

        var top20 = dedupedAssessments.Select(a => (
            subject: ResolveSubject(a.Subject, a.UploadType),
            type: a.UploadType,
            score: a.Score ?? 0.0,
            proficiency: a.Proficiency,
            period: a.Period ?? a.Date ?? "Unknown"
        ));

        // Task 30: fetch cached school averages (O(1) read from config container — BRD NF-PERF-3)
        var schoolAvg = await schoolAverages.GetAsync();

        // Task 32: fetch externalized prompt template from config container; null → inline default
        var promptConfig = await cosmos.GetPromptConfigAsync();

        var prompt = piiRedaction.BuildRedactedPrompt(
            studentId,
            top20,
            grade: student.Grade,
            promptTemplate: promptConfig?.Template,
            schoolElaAvg: schoolAvg?.ElaAvgProficiency ?? (schoolAvg?.ElaAvgScore.HasValue == true ? $"{schoolAvg.ElaAvgScore:F0}" : null),
            schoolMathAvg: schoolAvg?.MathAvgProficiency ?? (schoolAvg?.MathAvgScore.HasValue == true ? $"{schoolAvg.MathAvgScore:F0}" : null));

        string summaryText;
        try
        {
            summaryText = await llm.GenerateSummaryAsync(prompt, ct);
        }
        catch (Exception ex)
        {
            // Task 31: structured audit entry on LLM failure
            var errorType = ex is TaskCanceledException ? "timeout"
                          : ex is InvalidOperationException ? "config"
                          : ex is HttpRequestException ? "provider_error"
                          : "unknown";
            await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
                AuditEventType.Error, entityType: "AiSummary", entityId: studentId,
                details: $"LLM error [{errorType}] provider={llm.ModelName}: {ex.Message}",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString());
            var hint = errorType == "timeout" ? "The request timed out."
                     : errorType == "config" ? "The LLM API key is not configured."
                     : "The LLM provider rejected the request. Check the model name and API key.";
            return StatusCode(503, new { message = $"AI service unavailable ({llm.ModelName}). {hint}" });
        }

        var summary = new AiSummaryDocument
        {
            Id          = Guid.NewGuid().ToString(),
            StudentId   = studentId,
            SummaryText = summaryText,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
            ModelUsed   = llm.ModelName
        };

        await cosmos.CreateSummaryAsync(summary);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.AI, entityType: "AiSummary", entityId: studentId,
            details: $"AI summary generated (PII-free) model={llm.ModelName} promptVersion={promptConfig?.Version ?? "inline-default"}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(summary);
    }

    /// <summary>
    /// Resolves the display subject for the AI prompt. Handles "Mixed" IXL records and
    /// ensures Acadience/IREAD are always shown as "Reading" regardless of stored Subject value.
    /// </summary>
    private static string ResolveSubject(string? subject, string uploadType)
    {
        // Acadience and IREAD are always Reading — override whatever Subject was stored
        if (uploadType.Equals("Acadience", StringComparison.OrdinalIgnoreCase) ||
            uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase))
            return "Reading";

        if (subject is null || subject.Equals("Mixed", StringComparison.OrdinalIgnoreCase))
        {
            // IXL stores subject in Subject column — if missing, default to ELA (IXL covers ELA+Math)
            if (uploadType.Equals("IXL", StringComparison.OrdinalIgnoreCase)) return "ELA";
            return "Mixed";
        }

        return subject;
    }

    /// <summary>Admin endpoint to upsert the AI summary prompt template without a code release (Task 32).</summary>
    [HttpPut("prompt-config")]
    public async Task<IActionResult> UpsertPromptConfig([FromBody] PromptConfigDocument doc)
    {
        doc.UpdatedAt = DateTime.UtcNow.ToString("o");
        doc.UpdatedBy = CurrentAdminEmail;
        await cosmos.UpsertPromptConfigAsync(doc);
        return Ok(doc);
    }

    /// <summary>Returns the current AI prompt config (or null if using inline default).</summary>
    [HttpGet("prompt-config")]
    public async Task<IActionResult> GetPromptConfig()
    {
        var config = await cosmos.GetPromptConfigAsync();
        return Ok(config);
    }
}
