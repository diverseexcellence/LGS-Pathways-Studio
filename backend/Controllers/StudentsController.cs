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
    /// <summary>
    /// Sources a hand-entered record may claim. "demographics" is in the upload set because it is a
    /// file type, but it is roster data rather than an assessment result — a record carrying it
    /// would store an assessment with no performance level any ruleset can score.
    /// </summary>
    internal static readonly string[] ManualAssessmentSources = UploadController.SupportedUploadTypes
        .Where(t => !t.Equals("demographics", StringComparison.OrdinalIgnoreCase))
        .ToArray();

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

    /// <summary>
    /// Creates a single student by hand, optionally with their assessment history, and runs the
    /// tier engine over the result — the same normalization and the same engine an imported row
    /// goes through, so a hand-entered student is tiered identically to an ingested one.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStudentDto dto)
    {
        var name = dto.FullName?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            return BadRequest(new { message = "Full name is required." });

        var stn = Blank(dto.Stn);
        var dob = Blank(dto.Dob);
        var localId = Blank(dto.LocalId);

        // Same identity precedence the importer matches on (STN → localId → name+DOB), so a
        // student who would have been matched by a later upload isn't created as a second record.
        if (!dto.AllowDuplicate)
        {
            var clash = stn is not null ? await cosmos.FindStudentByStnAsync(stn) : null;
            clash ??= localId is not null ? await cosmos.FindStudentByLocalIdAsync(localId) : null;
            clash ??= dob is not null ? await cosmos.FindStudentByNameAndDobAsync(name, dob) : null;
            if (clash is not null && clash.IsActive)
                return Conflict(new
                {
                    message = $"{clash.FullName} already exists with a matching identifier. " +
                              "Open that profile to add records, or resubmit with allowDuplicate to create a second record anyway.",
                    studentId = clash.StudentId,
                });
        }

        var records = dto.Records ?? new List<ManualAssessmentDto>();
        foreach (var record in records)
        {
            var problem = ManualAssessmentFactory.Validate(record.ToInput(), ManualAssessmentSources);
            if (problem is not null) return BadRequest(new { message = problem });
        }

        var studentId = $"s-{Guid.NewGuid():N}";
        var student = new StudentDocument
        {
            Id          = studentId,
            StudentId   = studentId,
            FullName    = name,
            Dob         = dob,
            Stn         = stn,
            LocalId     = localId,
            ClassGroup  = Blank(dto.ClassGroup) ?? "Unassigned",
            Grade       = Blank(dto.Grade)?.TrimStart('0'),
            Gender      = Blank(dto.Gender),
            Ethnicity   = Blank(dto.Ethnicity),
            Race        = Blank(dto.Race),
            IsActive    = dto.IsActive ?? true,
            EllStatus   = Blank(dto.EllStatus),
            SpedStatus  = Blank(dto.SpedStatus),
            Section504  = Blank(dto.Section504),
            HomeRoom    = Blank(dto.HomeRoom),
            EntryDate   = Blank(dto.EntryDate),
            ExitDate    = Blank(dto.ExitDate),
            LunchStatus = Blank(dto.LunchStatus),
            ZipCode     = Blank(dto.ZipCode),
            SourceFile  = ManualAssessmentFactory.SourceLabel,
            EnrolDate   = DateTime.UtcNow.ToString("o"),
            LastUpdated = DateTime.UtcNow.ToString("o"),
        };
        await cosmos.UpsertStudentAsync(student);

        foreach (var record in records)
            await cosmos.CreateAssessmentAsync(
                ManualAssessmentFactory.Build(studentId, record.ToInput(), CurrentAdminEmail));

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "Student", entityId: studentId,
            details: $"Created student by hand: {name}" +
                     (records.Count > 0 ? $" with {records.Count} assessment record(s)" : "") +
                     (stn is not null ? $" | STN {stn}" : ""),
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        // Run the engine even with no records: that produces the Pending state and the
        // "no_assessments" reason the profile explains, rather than a blank tier with no reasoning.
        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        var created = await cosmos.GetStudentAsync(studentId) ?? student;
        return CreatedAtAction(nameof(Get), new { id = studentId }, created);
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
        if (dto.FullName is not null)
        {
            var name = dto.FullName.Trim();
            if (name.Length == 0) return BadRequest(new { message = "Full name cannot be blank." });
            student.FullName = name; changed.Add($"Name→{name}");
        }
        if (dto.ClassGroup is not null) { student.ClassGroup = dto.ClassGroup; changed.Add($"ClassGroup→{dto.ClassGroup}"); }
        if (dto.Grade is not null) { student.Grade = dto.Grade; changed.Add($"Grade→{dto.Grade}"); }
        if (dto.HomeRoom is not null) { student.HomeRoom = dto.HomeRoom; changed.Add($"HomeRoom→{dto.HomeRoom}"); }
        if (dto.Stn is not null) { student.Stn = dto.Stn; changed.Add($"STN→{dto.Stn}"); }
        if (dto.LocalId is not null) { student.LocalId = dto.LocalId; changed.Add($"LocalId→{dto.LocalId}"); }
        if (dto.Dob is not null) { student.Dob = dto.Dob; changed.Add($"DOB→{dto.Dob}"); }
        if (dto.Gender is not null) { student.Gender = dto.Gender; changed.Add($"Gender→{dto.Gender}"); }
        if (dto.Ethnicity is not null) { student.Ethnicity = dto.Ethnicity; changed.Add($"Ethnicity→{dto.Ethnicity}"); }
        if (dto.Race is not null) { student.Race = dto.Race; changed.Add($"Race→{dto.Race}"); }
        if (dto.IsActive is not null) { student.IsActive = dto.IsActive.Value; changed.Add($"Active→{dto.IsActive}"); }
        if (dto.EllStatus is not null) { student.EllStatus = dto.EllStatus; changed.Add($"ELL→{dto.EllStatus}"); }
        if (dto.SpedStatus is not null) { student.SpedStatus = dto.SpedStatus; changed.Add($"SPED→{dto.SpedStatus}"); }
        if (dto.Section504 is not null) { student.Section504 = dto.Section504; changed.Add($"504→{dto.Section504}"); }
        if (dto.LunchStatus is not null) { student.LunchStatus = dto.LunchStatus; changed.Add($"Lunch→{dto.LunchStatus}"); }
        if (dto.ZipCode is not null) { student.ZipCode = dto.ZipCode; changed.Add($"Zip→{dto.ZipCode}"); }
        if (dto.EntryDate is not null) { student.EntryDate = dto.EntryDate; changed.Add($"EntryDate→{dto.EntryDate}"); }
        if (dto.ExitDate is not null) { student.ExitDate = dto.ExitDate; changed.Add($"ExitDate→{dto.ExitDate}"); }
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

    // BRD ST-17 (per-subject): admin override of a single subject's tier. There is no
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
        // "Finalized" is still accepted so an older client can't be rejected mid-rollout; it is
        // normalized to the current name before being stored.
        if (dto.Status is not null &&
            dto.Status is not (TierStatus.Pending or TierStatus.SystemRecommended
                               or TierStatus.AdminOverride or TierStatus.LegacyFinalized))
            return BadRequest(new { message = "status must be 'Pending', 'System Recommended', or 'Admin Override'." });

        var priorTier = target.Tier;
        var priorStatus = target.Status;

        if (dto.Tier is not null) target.Tier = dto.Tier;
        if (dto.Status is not null)
            target.Status = TierStatus.IsAdminOverride(dto.Status) ? TierStatus.AdminOverride : dto.Status;
        target.OverriddenBy = CurrentAdminEmail;
        target.OverriddenAt = DateTime.UtcNow.ToString("o");
        student.LastUpdated = DateTime.UtcNow.ToString("o");

        await cosmos.UpsertStudentAsync(student);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.TierRecommendation, entityType: "Student", entityId: id,
            details: $"{subjectLabel} Tier Overridden by Admin — {student.FullName}: " +
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
    // Per-subject override gating: a subject an admin has overridden is left untouched by the
    // engine, so this only 400s when BOTH subjects are overridden (nothing left to compute).
    [HttpPost("{id}/recalculate-tier")]
    public async Task<IActionResult> RecalculateTier(string id)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        if (student.AllSubjectsOverridden)
            return BadRequest(new { message = "Both ELA and Math tiers are set by Admin Override. Change them from the tier selector instead." });

        // ComputeAndApplyAsync writes its own audit entry covering both subjects.
        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        student = (await cosmos.GetStudentAsync(id))!;
        return Ok(student);
    }

    // ─── Hand-entered assessment records ──────────────────────────────────────
    //
    // Every one of the three writes below re-runs the tier engine for the student, because a record
    // that changes the evidence without changing the recommendation on screen is worse than no
    // record at all — staff would act on a tier that no longer reflects the data they just entered.
    // Per-subject override gating lives inside the engine, so a subject an admin has overridden
    // keeps its tier while the other subject still updates.

    [HttpPost("{id}/assessments")]
    public async Task<IActionResult> CreateAssessment(string id, [FromBody] ManualAssessmentDto dto)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        var problem = ManualAssessmentFactory.Validate(dto.ToInput(), ManualAssessmentSources);
        if (problem is not null) return BadRequest(new { message = problem });

        var assessment = ManualAssessmentFactory.Build(student.StudentId, dto.ToInput(), CurrentAdminEmail);
        await cosmos.CreateAssessmentAsync(assessment);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "Assessment", entityId: student.StudentId,
            details: $"Added assessment record by hand for {student.FullName}: {Describe(assessment)}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        return Ok(new { assessment, student = await cosmos.GetStudentAsync(id) ?? student });
    }

    [HttpPut("{id}/assessments/{assessmentId}")]
    public async Task<IActionResult> UpdateAssessment(string id, string assessmentId, [FromBody] ManualAssessmentDto dto)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        var assessment = await cosmos.GetAssessmentAsync(student.StudentId, assessmentId);
        if (assessment is null) return NotFound();

        var problem = ManualAssessmentFactory.Validate(dto.ToInput(), ManualAssessmentSources);
        if (problem is not null) return BadRequest(new { message = problem });

        var before = Describe(assessment);
        // Imported records are editable too — a wrong performance level in a source export is the
        // most common reason staff need to correct one. The edit is normalized and audited the same
        // way either way; what it loses is the tie to its source file, so the record is re-stamped
        // as hand-entered rather than continuing to claim it came from that CSV.
        var wasImported = assessment.FileName != ManualAssessmentFactory.SourceLabel;
        ManualAssessmentFactory.Apply(assessment, dto.ToInput(), CurrentAdminEmail);
        if (wasImported) assessment.FileName = ManualAssessmentFactory.SourceLabel;
        assessment.UploadedAt = DateTime.UtcNow.ToString("o");
        await cosmos.UpsertAssessmentAsync(assessment);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Edit, entityType: "Assessment", entityId: student.StudentId,
            details: $"Edited assessment record for {student.FullName}: {before} → {Describe(assessment)}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        return Ok(new { assessment, student = await cosmos.GetStudentAsync(id) ?? student });
    }

    [HttpDelete("{id}/assessments/{assessmentId}")]
    public async Task<IActionResult> DeleteAssessment(string id, string assessmentId)
    {
        var student = await cosmos.GetStudentAsync(id);
        if (student is null || !student.IsActive) return NotFound();

        var assessment = await cosmos.GetAssessmentAsync(student.StudentId, assessmentId);
        if (assessment is null) return NotFound();

        await cosmos.DeleteAssessmentAsync(student.StudentId, assessmentId);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Delete, entityType: "Assessment", entityId: student.StudentId,
            details: $"Deleted assessment record for {student.FullName}: {Describe(assessment)}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        await tierCalculation.ComputeAndApplyAsync(student, CurrentAdminId, CurrentAdminEmail);

        return Ok(new { student = await cosmos.GetStudentAsync(id) ?? student });
    }

    private static string Describe(Models.AssessmentDocument a) =>
        $"{a.UploadType} {a.Subject} {a.Period ?? "no period"} " +
        $"\"{a.Proficiency ?? "no level"}\"" +
        (a.Score is not null ? $" score {a.Score}" : "") +
        (a.DateIso is not null ? $" on {a.DateIso}" : "");

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

// Every field is nullable and only applied when present, so a partial PATCH cannot blank out a
// column the client didn't send.
public record StudentUpdateDto(
    string? ClassGroup, string? Grade, string? HomeRoom, string? Stn, string? LocalId, string? Dob,
    string? FullName = null, string? Gender = null, string? Ethnicity = null, string? EllStatus = null,
    string? SpedStatus = null, string? Section504 = null, string? LunchStatus = null,
    string? ZipCode = null, string? EntryDate = null, string? ExitDate = null,
    string? Race = null, bool? IsActive = null);

public record CreateStudentDto(
    string? FullName, string? Dob = null, string? Stn = null, string? LocalId = null,
    string? ClassGroup = null, string? Grade = null, string? Gender = null, string? Ethnicity = null,
    string? EllStatus = null, string? SpedStatus = null, string? Section504 = null,
    string? HomeRoom = null, string? EntryDate = null, string? ExitDate = null,
    string? LunchStatus = null, string? ZipCode = null,
    List<ManualAssessmentDto>? Records = null,
    /// <summary>Set after the caller has seen the conflict response and chosen to proceed.</summary>
    bool AllowDuplicate = false,
    string? Race = null,
    bool? IsActive = null);

public record ManualAssessmentDto(
    string? UploadType, string? Subject = null, string? Period = null,
    double? Score = null, string? Proficiency = null, string? Date = null)
{
    public ManualAssessmentInput ToInput() =>
        new(UploadType ?? "", Subject, Period, Score, Proficiency, Date);
}
public record SetSubjectTierDto(string? Tier, string? Status, string? Note);
public record CreateNoteDto(string Text);
