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
        var oldClassGroup = student.ClassGroup;
        if (dto.ClassGroup is not null) { student.ClassGroup = dto.ClassGroup; changed.Add($"ClassGroup→{dto.ClassGroup}"); }
        if (dto.Grade is not null) { student.Grade = dto.Grade; changed.Add($"Grade→{dto.Grade}"); }
        if (dto.HomeRoom is not null) { student.HomeRoom = dto.HomeRoom; changed.Add($"HomeRoom→{dto.HomeRoom}"); }
        if (dto.Stn is not null) { student.Stn = dto.Stn; changed.Add($"STN→{dto.Stn}"); }
        if (dto.LocalId is not null) { student.LocalId = dto.LocalId; changed.Add($"LocalId→{dto.LocalId}"); }
        if (dto.Dob is not null) { student.Dob = dto.Dob; changed.Add($"DOB→{dto.Dob}"); }
        student.LastUpdated = DateTime.UtcNow.ToString("o");

        // classGroup is the Cosmos partition key — changing it without deleting the old
        // partition copy leaves phantom Unassigned duplicates in the directory.
        await cosmos.MoveStudentPartitionAsync(student, oldClassGroup);

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

    /// <summary>
    /// Copy STN and DOB from assessment rawFields onto students who were auto-created from IXL
    /// without identifiers. ILEARN rows store both even when the student record does not.
    /// STN and DOB are filled independently — a student who already has STN still gets DOB.
    /// </summary>
    [HttpPost("backfill-stn")]
    public async Task<IActionResult> BackfillStn()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
        var stnUpdated = 0;
        var dobUpdated = 0;
        var unmatched = 0;

        foreach (var student in students)
        {
            var needStn = string.IsNullOrWhiteSpace(student.Stn);
            var needDob = string.IsNullOrWhiteSpace(student.Dob);
            if (!needStn && !needDob) continue;

            var assessments = await cosmos.GetAssessmentsAsync(student.StudentId);
            string? stn = null;
            string? dob = null;
            foreach (var a in assessments)
            {
                if (needStn)
                    stn ??= ExtractRaw(a.RawFields, "STN", "State_StudentNumber", "State Student Number",
                        "SSID", "ILEARN Student ID", "Student State ID", "Statewide Student ID");
                if (needDob)
                    dob ??= ExtractRaw(a.RawFields, "DOB", "Date of Birth", "Birth Date", "Student DOB");
                if ((!needStn || stn is not null) && (!needDob || dob is not null)) break;
            }

            var dirty = false;
            if (needStn && !string.IsNullOrWhiteSpace(stn)) { student.Stn = stn; stnUpdated++; dirty = true; }
            if (needDob && !string.IsNullOrWhiteSpace(dob)) { student.Dob = dob; dobUpdated++; dirty = true; }
            if (!dirty) { unmatched++; continue; }

            student.LastUpdated = DateTime.UtcNow.ToString("o");
            await cosmos.UpsertStudentAsync(student);
        }

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "StudentList",
            details: $"Backfilled identifiers from assessments — STN {stnUpdated}, DOB {dobUpdated}, unmatched {unmatched}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(new { stnUpdated, dobUpdated, unmatched });
    }

    private static string? ExtractRaw(Dictionary<string, string> raw, params string[] keys)
    {
        foreach (var key in keys)
        {
            var match = raw.Keys.FirstOrDefault(k =>
                k.Equals(key, StringComparison.OrdinalIgnoreCase) ||
                k.EndsWith("." + key, StringComparison.OrdinalIgnoreCase) ||
                (k.Contains(key, StringComparison.OrdinalIgnoreCase) &&
                 !k.Contains("name", StringComparison.OrdinalIgnoreCase)));
            if (match is null) continue;
            var value = raw[match].Trim();
            if (string.IsNullOrWhiteSpace(value)) continue;
            if (value.Equals("[REDACTED]", StringComparison.OrdinalIgnoreCase)) continue;
            if (value.Equals("N/A", StringComparison.OrdinalIgnoreCase)) continue;
            return value;
        }
        return null;
    }
}

public record StudentUpdateDto(string? ClassGroup, string? Grade, string? HomeRoom, string? Stn, string? LocalId, string? Dob);
public record SetSubjectTierDto(string? Tier, string? Status, string? Note);
public record CreateNoteDto(string Text);
