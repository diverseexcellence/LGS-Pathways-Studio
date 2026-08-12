using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/students")]
[Authorize]
public class StudentsController(ICosmosDbService cosmos, IAuditService audit, ITierCalculationService tierCalculation) : ControllerBase
{
    private int CurrentAdminId => int.Parse(User.FindFirstValue("adminId") ?? "0");
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? search = null,
        [FromQuery] string? classGroup = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] string? sortDir = null)
    {
        pageSize = Math.Min(pageSize, 500);
        var (items, total) = await cosmos.ListStudentsAsync(page, pageSize, search, classGroup, sortBy: sortBy, sortDir: sortDir);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.View, entityType: "StudentList",
            details: $"Viewed student list — page {page}, search='{search}'",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(new { items, total, page, pageSize });
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.View, entityType: "Student", entityId: id,
            details: $"Viewed profile: {student.FullName}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(student);
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] StudentUpdateDto dto)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        // Capture prior tier values before mutation (needed for finalize audit entry)
        var priorTier = student.Tier;
        var priorTierStatus = student.TierStatus;

        var changed = new List<string>();
        if (dto.ClassGroup is not null) { student.ClassGroup = dto.ClassGroup; changed.Add($"ClassGroup→{dto.ClassGroup}"); }
        if (dto.Grade is not null) { student.Grade = dto.Grade; changed.Add($"Grade→{dto.Grade}"); }
        if (dto.Tier is not null) { student.Tier = dto.Tier; changed.Add($"Tier→{dto.Tier}"); }
        if (dto.TierStatus is not null) { student.TierStatus = dto.TierStatus; changed.Add($"TierStatus→{dto.TierStatus}"); }
        if (dto.HomeRoom is not null) { student.HomeRoom = dto.HomeRoom; changed.Add($"HomeRoom→{dto.HomeRoom}"); }
        student.LastUpdated = DateTime.UtcNow.ToString("o");

        await cosmos.UpsertStudentAsync(student);

        // BRD ST-17: finalize produces a dedicated audit entry with prior→new tier values
        string auditDetails;
        AuditEventType auditEventType;
        if (dto.TierStatus == "Finalized" && dto.Tier is not null)
        {
            auditDetails = $"Tier Overridden / Finalized by Admin — {student.FullName}: " +
                           $"Prior: {priorTier} ({priorTierStatus}) → New: {student.Tier} (Finalized)";
            auditEventType = AuditEventType.TierRecommendation;
        }
        else
        {
            auditDetails = $"Edited {student.FullName}: {string.Join(", ", changed)}";
            auditEventType = AuditEventType.Edit;
        }

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            auditEventType, entityType: "Student", entityId: id,
            details: auditDetails,
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(student);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null) return NotFound();

        student.IsActive = false;
        student.LastUpdated = DateTime.UtcNow.ToString("o");
        await cosmos.UpsertStudentAsync(student);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Delete, entityType: "Student", entityId: id,
            details: $"Soft-deleted student: {student.FullName}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return NoContent();
    }
    // BRD ST-16 / Generate Recommendation button: recalculate tier for a single student
    [HttpPost("{id}/recalculate-tier")]
    public async Task<IActionResult> RecalculateTier(string id)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        // Don't overwrite a Finalized tier
        if (student.TierStatus == "Finalized")
            return BadRequest(new { message = "Tier is already Finalized. Use Override to change it." });

        var priorTier = student.Tier;
        var priorStatus = student.TierStatus;

        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        // Re-fetch to return updated state
        student = (await cosmos.GetStudentAsync(id))!;

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.TierRecommendation, entityType: "Student", entityId: id,
            details: $"System Tier Recommendation Generated — {student.FullName}: " +
                     $"Prior: {priorTier} ({priorStatus}) → New: {student.Tier} ({student.TierStatus})",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(student);
    }

    // BRD ST-21 / NF-AUD-1: per-student audit log — accessible to all admins (not super-admin-only)
    [HttpGet("{id}/audit")]
    public async Task<IActionResult> GetAudit(string id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        var (items, total) = await cosmos.GetAuditLogsByEntityIdAsync(id, page, pageSize);
        return Ok(new { items, total, page, pageSize });
    }

    // ─── Collaboration Notes (BRD ST-20) ──────────────────────────────────────

    [HttpGet("{id}/notes")]
    public async Task<IActionResult> GetNotes(string id)
    {
        var notes = await cosmos.GetNotesAsync(id);
        return Ok(notes);
    }

    [HttpPost("{id}/notes")]
    public async Task<IActionResult> CreateNote(string id, [FromBody] CreateNoteDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Text))
            return BadRequest(new { message = "Note text is required." });

        var note = new LgsImpact.Api.Models.CollaborationNoteDocument
        {
            Id        = $"note-{Guid.NewGuid():N}",
            StudentId = id,
            Text      = dto.Text.Trim(),
            CreatedAt = DateTime.UtcNow.ToString("o"),
            CreatedBy = CurrentAdminEmail,
        };

        await cosmos.CreateNoteAsync(note);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "CollaborationNote", entityId: id,
            details: $"Added collaboration note for student {id}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(note);
    }

    [HttpDelete("{id}/notes/{noteId}")]
    public async Task<IActionResult> DeleteNote(string id, string noteId)
    {
        var note = await cosmos.GetNoteAsync(id, noteId);
        if (note is null) return NotFound();

        note.IsDeleted  = true;
        note.DeletedAt  = DateTime.UtcNow.ToString("o");
        note.DeletedBy  = CurrentAdminEmail;
        await cosmos.UpsertNoteAsync(note);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Delete, entityType: "CollaborationNote", entityId: id,
            details: $"Deleted collaboration note {noteId} for student {id}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return NoContent();
    }

    [HttpDelete("cleanup-numeric-names")]
    public async Task<IActionResult> CleanupNumericNames()
    {
        var deleted = await cosmos.DeleteStudentsWhereNameIsNumericAsync();
        return Ok(new { deleted });
    }

    [HttpPost("deduplicate")]
    public async Task<IActionResult> Deduplicate()
    {
        var merged = await cosmos.DeduplicateStudentsAsync();
        return Ok(new { merged });
    }
}

public record StudentUpdateDto(string? ClassGroup, string? Grade, string? Tier, string? TierStatus, string? HomeRoom);
public record CreateNoteDto(string Text);
