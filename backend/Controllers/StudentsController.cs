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

    // Demographics-only. Tier overrides go through PUT /api/students/{id}/tier/{subject} —
    // there is no combined tier to set here (TR-011, AC-08).
    [HttpPatch("{id}")]
    public async Task<IActionResult> Update(string id, [FromBody] StudentUpdateDto dto)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        var changed = new List<string>();
        if (dto.ClassGroup is not null) { student.ClassGroup = dto.ClassGroup; changed.Add($"ClassGroup→{dto.ClassGroup}"); }
        if (dto.Grade is not null) { student.Grade = dto.Grade; changed.Add($"Grade→{dto.Grade}"); }
        if (dto.HomeRoom is not null) { student.HomeRoom = dto.HomeRoom; changed.Add($"HomeRoom→{dto.HomeRoom}"); }
        student.LastUpdated = DateTime.UtcNow.ToString("o");

        await cosmos.UpsertStudentAsync(student);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "Student", entityId: id,
            details: $"Edited {student.FullName}: {string.Join(", ", changed)}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(student);
    }

    // BRD ST-17 (per-subject): admin override / finalize of a single subject's tier. There is no
    // combined overall tier to set (TR-011, AC-08) — ELA and Math are overridden independently.
    [HttpPut("{id}/tier/{subject}")]
    public async Task<IActionResult> SetSubjectTier(string id, string subject, [FromBody] SetSubjectTierDto dto)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        SubjectTier target;
        string subjectLabel;
        if (string.Equals(subject, "ela", StringComparison.OrdinalIgnoreCase)) { target = student.ElaTier; subjectLabel = "ELA"; }
        else if (string.Equals(subject, "math", StringComparison.OrdinalIgnoreCase)) { target = student.MathTier; subjectLabel = "Math"; }
        else return BadRequest(new { message = "subject must be 'ela' or 'math'." });

        if (dto.Tier is not null && dto.Tier is not ("Tier 1" or "Tier 2" or "Tier 3"))
            return BadRequest(new { message = "tier must be 'Tier 1', 'Tier 2', or 'Tier 3'." });
        if (dto.Status is not null && dto.Status is not ("Pending" or "System Recommended" or "Finalized"))
            return BadRequest(new { message = "status must be 'Pending', 'System Recommended', or 'Finalized'." });

        var priorTier = target.Tier;
        var priorStatus = target.Status;

        if (dto.Tier is not null) target.Tier = dto.Tier;
        if (dto.Status is not null) target.Status = dto.Status;
        target.OverriddenBy = CurrentAdminEmail;
        target.OverriddenAt = DateTime.UtcNow.ToString("o");
        student.LastUpdated = DateTime.UtcNow.ToString("o");

        await cosmos.UpsertStudentAsync(student);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.TierRecommendation, entityType: "Student", entityId: id,
            details: $"{subjectLabel} Tier Overridden / Finalized by Admin — {student.FullName}: " +
                     $"Prior: {priorTier ?? "Pending"} ({priorStatus}) → New: {target.Tier ?? "Pending"} ({target.Status})" +
                     (dto.Note is not null ? $" | Note: {dto.Note}" : ""),
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
    // BRD ST-16 / Generate Recommendation button: recalculate tier for a single student.
    // Per-subject Finalized gating: a subject already Finalized is left untouched by the engine,
    // so this only 400s when BOTH subjects are Finalized (nothing left to compute).
    [HttpPost("{id}/recalculate-tier")]
    public async Task<IActionResult> RecalculateTier(string id)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        if (student.AllSubjectsFinalized)
            return BadRequest(new { message = "Both ELA and Math tiers are Finalized. Use Override to change them." });

        // ComputeAndApplyAsync writes its own audit entry covering both subjects.
        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        student = (await cosmos.GetStudentAsync(id))!;
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

public record StudentUpdateDto(string? ClassGroup, string? Grade, string? HomeRoom);
public record SetSubjectTierDto(string? Tier, string? Status, string? Note);
public record CreateNoteDto(string Text);
