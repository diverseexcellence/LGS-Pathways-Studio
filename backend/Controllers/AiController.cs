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

        var top20 = assessments.Take(20).Select(a => (
            subject: a.Subject ?? "Mixed",
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
            var errorType = ex is HttpRequestException ? "connection_failed"
                          : ex is TaskCanceledException  ? "timeout"
                          : "unknown";
            await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
                AuditEventType.Error, entityType: "AiSummary", entityId: studentId,
                details: $"LLM error [{errorType}] provider={llm.ModelName}: {ex.Message}",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString());
            return StatusCode(503, new { message = $"AI service unavailable. Provider: {llm.ModelName}. Check that the LLM service is running." });
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
