using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/ai")]
[Authorize]
public class AiController(ICosmosDbService cosmos, ILlmService llm, IPiiRedactionService piiRedaction, IAuditService audit) : ControllerBase
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

        var prompt = piiRedaction.BuildRedactedPrompt(int.Parse(studentId.Replace("s-", "").Replace("-", "")), top20);

        string summaryText;
        try
        {
            summaryText = await llm.GenerateSummaryAsync(prompt, ct);
        }
        catch (Exception ex)
        {
            await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
                AuditEventType.Error, entityType: "AiSummary", entityId: studentId,
                details: $"LLM error: {ex.Message}",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString());
            return StatusCode(503, new { message = "AI service unavailable. Ensure Ollama is running (`ollama run llama3.2`)." });
        }

        var summary = new AiSummaryDocument
        {
            Id          = Guid.NewGuid().ToString(),
            StudentId   = studentId,
            SummaryText = summaryText,
            GeneratedAt = DateTime.UtcNow.ToString("o"),
            ModelUsed   = "llama3.2"
        };

        await cosmos.CreateSummaryAsync(summary);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.AI, entityType: "AiSummary", entityId: studentId,
            details: "AI summary generated (PII-free via Ollama)",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(summary);
    }
}
