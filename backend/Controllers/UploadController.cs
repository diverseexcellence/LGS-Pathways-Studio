using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using CsvHelper;
using System.Globalization;
using OfficeOpenXml;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/upload")]
[Authorize]
public class UploadController(ICosmosDbService cosmos, IBlobStorageService blob, IAuditService audit, IPiiRedactionService piiRedaction, ISchoolAverageService schoolAverages, ITierCalculationService tierCalculation) : ControllerBase
{
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";
    private int CurrentAdminId => int.Parse(User.FindFirstValue("adminId") ?? "0");

    [HttpPost]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload(IFormFile file, [FromForm] string uploadType, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(new { message = "No file provided" });
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext is not ".csv" and not ".xlsx") return BadRequest(new { message = "Only .csv and .xlsx files accepted" });

        // ── Task 34: duplicate file prevention via SHA-256 content hash ──────────
        var contentHash = ComputeSha256(file);
        var existingLog = await cosmos.FindUploadLogByHashAsync(contentHash);
        if (existingLog is not null)
            return Conflict(new { message = $"Duplicate file detected. This file was already uploaded on {existingLog.UploadedAt} as \"{existingLog.FileName}\"." });

        List<Dictionary<string, string>> rows;
        try { rows = ext == ".csv" ? await ParseCsvAsync(file, ct) : ParseXlsx(file); }
        catch (Exception ex) { return BadRequest(new { message = $"Parse error: {ex.Message}" }); }

        if (rows.Count == 0) return BadRequest(new { message = "The file contains no data rows." });

        var headers = rows[0].Keys.ToList();

        // ── Task 33: required columns check ──────────────────────────────────
        var schemaError = ValidateSchema(headers, uploadType);
        if (schemaError is not null)
            return BadRequest(new { message = $"CSV schema error for \"{uploadType}\": {schemaError}" });

        // ── Task 35: file-type mismatch detection ─────────────────────────────
        var detectedType = DetectTypeFromColumns(headers);
        if (detectedType is not null && !detectedType.Equals(uploadType, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { message = $"File-type mismatch: the file appears to be \"{detectedType}\" data but you selected \"{uploadType}\". Please choose the correct Data Type before uploading." });

        string? blobUrl = null;
        try
        {
            using var stream = file.OpenReadStream();
            var blobName = await blob.UploadAsync(stream, file.FileName, file.ContentType, ct);
            blobUrl = await blob.GetSasUrlAsync(blobName);
        }
        catch { /* optional in dev */ }

        var result = await ProcessRowsAsync(rows, uploadType, file.FileName, ct);

        await cosmos.CreateUploadLogAsync(new UploadLogDocument
        {
            Id           = Guid.NewGuid().ToString(),
            UploadedBy   = CurrentAdminEmail,
            FileName     = file.FileName,
            UploadType   = uploadType,
            UploadedAt   = DateTime.UtcNow.ToString("o"),
            RecordCount  = result.ImportedRows,
            SkippedCount = result.SkippedRows,
            Errors       = result.Errors,
            BlobUrl      = blobUrl,
            ContentHash  = contentHash
        });

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Upload, entityType: "Upload", entityId: file.FileName,
            details: $"Uploaded {file.FileName} ({uploadType}) — {result.ImportedRows} rows, {result.SkippedRows} skipped",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        // Refresh cached school averages after any assessment upload (fire-and-forget, non-blocking)
        _ = Task.Run(() => schoolAverages.RefreshAsync());

        // Recompute tier recommendations for all active students after any upload
        // (demographics adds new students; assessments change available signals)
        _ = Task.Run(async () =>
        {
            var (students, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null, activeOnly: true);
            foreach (var student in students.Where(s => s.TierStatus != TierStatus.Finalized))
                await tierCalculation.ComputeAndApplyAsync(student);
        });

        return Ok(result);
    }

    [HttpPost("import-landing-zone")]
    public async Task<IActionResult> ImportLandingZone(CancellationToken ct)
    {
        List<LandingZoneFile> files;
        try { files = await blob.ListLandingZoneFilesAsync(ct); }
        catch (Exception ex) { return BadRequest(new { message = $"Could not access landing-zone: {ex.Message}" }); }

        if (files.Count == 0) return Ok(new { message = "No CSV or Excel files found in landing-zone.", results = Array.Empty<object>() });

        var results = new List<object>();
        foreach (var file in files)
        {
            try
            {
                var name = file.Name;
                var stream = file.Content;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                var uploadType = DetectUploadType(name);

                if (uploadType == "__SKIP__")
                {
                    results.Add(new { file = name, uploadType = "skipped", result = (object?)null, error = (string?)null });
                    await stream.DisposeAsync();
                    continue;
                }

                List<Dictionary<string, string>> rows;
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                var formFile = new StreamFormFile(buffer.ToArray(), name,
                    ext == ".csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

                // This path previously ran no duplicate, schema or type checks at all — so every
                // invocation re-imported every file in the container, multiplying assessment rows.
                // It now applies the same three guards as the manual upload endpoint.
                var contentHash = ComputeSha256(formFile);
                var existingLog = await cosmos.FindUploadLogByHashAsync(contentHash);
                if (existingLog is not null)
                {
                    results.Add(new { file = name, uploadType = "duplicate", result = (object?)null,
                                      error = $"Already imported on {existingLog.UploadedAt} as \"{existingLog.FileName}\"." });
                    continue;
                }

                rows = ext == ".csv" ? await ParseCsvAsync(formFile, ct) : ParseXlsx(formFile);

                var headers = rows.Count > 0 ? rows[0].Keys.ToList() : new List<string>();
                var schemaError = ValidateSchema(headers, uploadType);
                if (schemaError is not null)
                {
                    results.Add(new { file = name, uploadType, result = (object?)null,
                                      error = $"CSV schema error for \"{uploadType}\": {schemaError}" });
                    continue;
                }

                var detectedType = DetectTypeFromColumns(headers);
                if (detectedType is not null && !detectedType.Equals(uploadType, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new { file = name, uploadType, result = (object?)null,
                                      error = $"File-type mismatch: appears to be \"{detectedType}\" data but was detected as \"{uploadType}\" from the filename." });
                    continue;
                }

                var result = await ProcessRowsAsync(rows, uploadType, name, ct);

                await cosmos.CreateUploadLogAsync(new UploadLogDocument
                {
                    Id           = Guid.NewGuid().ToString(),
                    UploadedBy   = CurrentAdminEmail,
                    FileName     = name,
                    UploadType   = uploadType,
                    UploadedAt   = DateTime.UtcNow.ToString("o"),
                    RecordCount  = result.ImportedRows,
                    SkippedCount = result.SkippedRows,
                    ContentHash  = contentHash,
                    Errors       = result.Errors
                });

                await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
                    AuditEventType.Upload, entityType: "Upload", entityId: name,
                    details: $"Landing zone import: {name} ({uploadType}) — {result.ImportedRows} rows, {result.SkippedRows} skipped",
                    ip: HttpContext.Connection.RemoteIpAddress?.ToString());

                results.Add(new { file = name, uploadType, result });
            }
            catch (Exception ex)
            {
                results.Add(new { file = file.Name, error = ex.Message });
            }
            finally
            {
                await file.Content.DisposeAsync();
            }
        }

        // Refresh school averages and recompute tiers for all non-Finalized students
        _ = Task.Run(() => schoolAverages.RefreshAsync());
        _ = Task.Run(async () =>
        {
            var (students, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null, activeOnly: true);
            foreach (var student in students.Where(s => s.TierStatus != TierStatus.Finalized))
                await tierCalculation.ComputeAndApplyAsync(student);
        });

        return Ok(new { message = $"Processed {files.Count} file(s) from landing-zone.", results });
    }

    private static string DetectUploadType(string fileName)
    {
        var n = fileName.ToUpperInvariant();
        if (n.StartsWith("TEST_")) return "__SKIP__";
        if (n.Contains("ILEARN") || n.Contains("CHECKPOINT")) return "ILEARN";
        // "LevelUp" is IXL's product name — newer benchmark exports drop "IXL" from the filename
        // (e.g. "LevelUp-Diagnostic-Results-EOY-Math-LevelUp-Benchmark-By-Student-2026-06-19.csv").
        if (n.Contains("IXL") || n.Contains("LEVELUP") || n.Contains("LEVEL-UP")) return "IXL";
        if (n.Contains("ACADIENCE")) return "Acadience";
        if (n.Contains("IREAD") || n.Contains("I-READ")) return "IREAD";
        if (n.Contains("ALO") || n.Contains("READING_PM") || n.Contains("READING-PM")) return "Acadience";
        return "demographics";
    }

    private sealed class StreamFormFile : IFormFile
    {
        // Buffers the content rather than wrapping the source stream directly. The landing-zone
        // flow now reads each file twice (once to hash, once to parse), and ComputeSha256 disposes
        // the stream it is handed — so OpenReadStream must hand out an independent, rewound reader
        // each time rather than the single forward-only blob stream.
        private readonly byte[] _content;
        private readonly string _fileName;
        private readonly string _contentType;

        public StreamFormFile(byte[] content, string fileName, string contentType)
        {
            _content = content;
            _fileName = fileName;
            _contentType = contentType;
        }

        public string ContentType => _contentType;
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{_fileName}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _content.Length;
        public string Name => "file";
        public string FileName => _fileName;
        public void CopyTo(Stream target) => target.Write(_content, 0, _content.Length);
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => target.WriteAsync(_content, ct).AsTask();
        public Stream OpenReadStream() => new MemoryStream(_content, writable: false);
    }

    [HttpPost("recalculate-tiers")]
    public async Task<IActionResult> RecalculateTiers()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null, activeOnly: true);
        var eligible = students.Where(s => s.TierStatus != TierStatus.Finalized).ToList();
        foreach (var student in eligible)
            await tierCalculation.ComputeAndApplyAsync(student);
        return Ok(new { message = $"Tier recalculation complete.", processed = eligible.Count });
    }

    [HttpGet("logs")]
    public async Task<IActionResult> Logs()
    {
        var logs = await cosmos.GetUploadLogsAsync();
        return Ok(logs);
    }

    [HttpDelete("logs/{id}")]
    public async Task<IActionResult> DeleteLog(string id)
    {
        var log = await cosmos.GetUploadLogAsync(id);
        if (log is null) return NotFound();

        await cosmos.DeleteAssessmentsByFileNameAsync(log.FileName);
        await cosmos.DeleteUploadLogAsync(id, log.UploadedBy);

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Delete, entityType: "UploadLog", entityId: id,
            details: $"Deleted upload log: {log.FileName}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return NoContent();
    }

    private async Task<ParseSummaryDto> ProcessRowsAsync(
        List<Dictionary<string, string>> rows, string uploadType, string fileName, CancellationToken ct)
    {
        int imported = 0, skipped = 0, duplicateAssessments = 0;
        var errors = new List<string>();

        // Signatures of assessments already recorded for each student, loaded lazily on first
        // sight of that student and updated as we go. Guards against the same test event being
        // stored twice — whether from re-importing a file (the landing-zone path had no
        // duplicate protection at all) or from the client's overlapping exports, where one
        // result legitimately appears in both a subject-specific and a combined "Results" file.
        var seenAssessments = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            try
            {
                // Build full name — try exact combined-name columns first
                var name = GetValExact(row, "FullName", "Full Name", "Name", "Student Name",
                                       "Student Legal Name", "Legal Name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    var first = GetValExact(row, "First name", "First Name", "Student First Name", "FirstName",
                                            "Given Name", "Legal First Name", "Student Legal First Name");
                    var last  = GetValExact(row, "Last name",  "Last Name",  "Student Last Name",  "LastName",
                                            "Family Name", "Surname", "Legal Last Name", "Student Legal Last Name");
                    if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(last))
                        name = $"{first} {last}";
                }
                // Handle "Last, First" combined format (some IXL exports)
                if (!string.IsNullOrWhiteSpace(name) && name.Contains(',') && !name.Contains(' '))
                {
                    var parts = name.Split(',', 2);
                    if (parts.Length == 2) name = $"{parts[1].Trim()} {parts[0].Trim()}";
                }
                // For non-demographics, skip if no name. Demographics rows may still enrich via STN.
                if (string.IsNullOrWhiteSpace(name) && uploadType != "demographics") { skipped++; continue; }

                if (uploadType == "demographics")
                {
                    var dob = GetVal(row, "DOB", "Date of Birth", "Birth Date", "Student DOB",
                                     "STUDENTS.DOB") ?? "";
                    var stn = GetVal(row, "STN", "State_StudentNumber",
                                     "Student State ID", "STUDENTS.State_StudentNumber");
                    var localId = GetVal(row, "Student_Number", "STUDENTS.Student_Number",
                                         "Student Number", "Local Student ID");

                    // Match priority: LocalId → STN → name+dob
                    StudentDocument? existing = null;
                    if (!string.IsNullOrWhiteSpace(localId))
                        existing = await cosmos.FindStudentByLocalIdAsync(localId);
                    if (existing is null && !string.IsNullOrWhiteSpace(stn))
                        existing = await cosmos.FindStudentByStnAsync(stn);
                    if (existing is null && !string.IsNullOrWhiteSpace(name))
                        existing = await cosmos.FindStudentByNameAndDobAsync(name, dob);

                    if (existing is not null)
                    {
                        // Enrich existing student with demographic fields
                        existing.Dob        = dob.Length > 0 ? dob : existing.Dob;
                        existing.Stn        = stn ?? existing.Stn;
                        existing.LocalId    = localId ?? existing.LocalId;
                        var oldClassGroup   = existing.ClassGroup;
                        existing.ClassGroup = GetVal(row, "Class", "ClassGroup", "Class Group",
                                                    "Home_Room", "Homeroom", "STUDENTS.Home_Room") ?? existing.ClassGroup;
                        existing.Grade      = GetVal(row, "Grade", "Grade_Level", "Enrolled Grade",
                                                    "STUDENTS.Grade_Level", "Student Grade Level")?.TrimStart('0') ?? existing.Grade;
                        existing.Gender     = GetVal(row, "Gender", "STUDENTS.Gender") ?? existing.Gender;
                        existing.Ethnicity  = GetVal(row, "Ethnicity", "Race", "STUDENTS.Ethnicity",
                                                    "Race/Ethnicity", "STUDENTS.FedEthnicity") ?? existing.Ethnicity;
                        existing.EllStatus  = GetVal(row, "ELL", "English Learner", "ELL Status",
                                                    "Identified English Learner Status",
                                                    "S_STU_CRDC_X.englishlearner_yn") ?? existing.EllStatus;
                        existing.SpedStatus = GetVal(row, "SPED", "Special Education",
                                                    "Special Education Status",
                                                    "S_IN_STU_X.special_education_tf") ?? existing.SpedStatus;
                        existing.Section504 = GetVal(row, "504", "Section 504", "Section 504 Status",
                                                    "S_STU_CRDC_X.section504_yn",
                                                    "U_STUDENT_CUSTOM_ALERT_INFO.alert_504") ?? existing.Section504;
                        existing.HomeRoom    = GetVal(row, "HomeRoom", "Home Room", "Home_Room",
                                                     "STUDENTS.Home_Room") ?? existing.HomeRoom;
                        existing.EntryDate   = GetVal(row, "EntryDate", "Entry Date", "Entry_Date",
                                                      "STUDENTS.EntryDate", "Enrollment Date") ?? existing.EntryDate;
                        existing.ExitDate    = GetVal(row, "ExitDate", "Exit Date", "Exit_Date",
                                                      "STUDENTS.ExitDate") ?? existing.ExitDate;
                        existing.LunchStatus = GetVal(row, "LunchStatus", "Lunch Status", "Lunch_Status",
                                                      "Lunch Program", "Free/Reduced Lunch",
                                                      "STUDENTS.LunchStatus") ?? existing.LunchStatus;
                        existing.ZipCode     = GetVal(row, "ZipCode", "Zip Code", "Zip", "ZIP",
                                                      "Postal Code", "STUDENTS.Zip",
                                                      "STUDENTS.Zip_Code") ?? existing.ZipCode;
                        // Track the most recent file that supplied demographic data, not just
                        // whichever upload originally created the record — see QA issue #11.
                        existing.SourceFile  = fileName;
                        existing.LastUpdated = DateTime.UtcNow.ToString("o");
                        await cosmos.MoveStudentPartitionAsync(existing, oldClassGroup);
                        imported++;
                        continue;
                    }

                    // No existing student — only create if we have a name to identify them
                    if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; } // PowerSchool rows with no name columns

                    var studentId = $"s-{Guid.NewGuid():N}";
                    await cosmos.UpsertStudentAsync(new StudentDocument
                    {
                        Id         = studentId,
                        StudentId  = studentId,
                        FullName   = name,
                        Dob        = dob,
                        Stn        = stn,
                        LocalId    = localId,
                        ClassGroup = GetVal(row, "Class", "ClassGroup", "Class Group",
                                           "Home_Room", "Homeroom", "STUDENTS.Home_Room") ?? "Unassigned",
                        Grade      = GetVal(row, "Grade", "Grade_Level", "Enrolled Grade",
                                           "STUDENTS.Grade_Level", "Student Grade Level")?.TrimStart('0'),
                        Gender     = GetVal(row, "Gender", "STUDENTS.Gender"),
                        Ethnicity  = GetVal(row, "Ethnicity", "Race", "STUDENTS.Ethnicity",
                                           "Race/Ethnicity", "STUDENTS.FedEthnicity"),
                        EllStatus  = GetVal(row, "ELL", "English Learner", "ELL Status",
                                           "Identified English Learner Status",
                                           "S_STU_CRDC_X.englishlearner_yn"),
                        SpedStatus = GetVal(row, "SPED", "Special Education",
                                           "Special Education Status",
                                           "S_IN_STU_X.special_education_tf"),
                        Section504 = GetVal(row, "504", "Section 504", "Section 504 Status",
                                           "S_STU_CRDC_X.section504_yn",
                                           "U_STUDENT_CUSTOM_ALERT_INFO.alert_504"),
                        HomeRoom    = GetVal(row, "HomeRoom", "Home Room", "Home_Room",
                                            "STUDENTS.Home_Room"),
                        EntryDate   = GetVal(row, "EntryDate", "Entry Date", "Entry_Date",
                                            "STUDENTS.EntryDate", "Enrollment Date"),
                        ExitDate    = GetVal(row, "ExitDate", "Exit Date", "Exit_Date",
                                            "STUDENTS.ExitDate"),
                        LunchStatus = GetVal(row, "LunchStatus", "Lunch Status", "Lunch_Status",
                                            "Lunch Program", "Free/Reduced Lunch",
                                            "STUDENTS.LunchStatus"),
                        ZipCode     = GetVal(row, "ZipCode", "Zip Code", "Zip", "ZIP",
                                            "Postal Code", "STUDENTS.Zip",
                                            "STUDENTS.Zip_Code"),
                        SourceFile = fileName,
                        Tier       = "Pending",
                        TierStatus = "Pending",
                        EnrolDate  = DateTime.UtcNow.ToString("o"),
                        LastUpdated = DateTime.UtcNow.ToString("o")
                    });
                }
                else
                {
                    // Match student — try STN first, then localId, then name
                    StudentDocument? student = null;
                    var stn = GetVal(row, "STN", "Student State ID", "State_StudentNumber",
                                     "State Student Number", "State ID", "SSID",
                                     "Student State Number", "State Student ID Number",
                                     "Student ID", "ILEARN Student ID", "IREAD Student ID",
                                     "Statewide Student ID", "State Student Identifier");
                    var localId = GetVal(row, "Student_Number", "Student Number",
                                         "Local ID", "Local Student ID", "School ID",
                                         "Local Student Number");

                    // IXL exports are inconsistent: the Feb-2026 Diagnostic format puts a real STN in the
                    // "ID" column, while the Jul-2026 format puts IXL's own 7-digit internal ID there.
                    // Per BRD v1.2 DI-12, "ID" is only treated as an STN when the value has the shape of
                    // one. A non-STN value is deliberately NOT kept as a local id — IXL's internal id is
                    // not a school identifier and could collide with a real PowerSchool Student_Number in
                    // the local-id lookup below, attaching the row to the wrong student. Leaving it unset
                    // makes matching fall through to the name lookup instead.
                    if (string.IsNullOrWhiteSpace(stn) && uploadType == "IXL")
                    {
                        var idVal = GetVal(row, "ID");
                        if (LooksLikeStn(idVal)) stn = idVal;
                    }
                    else if (string.IsNullOrWhiteSpace(localId))
                        localId = GetVal(row, "ID");

                    if (!string.IsNullOrWhiteSpace(stn))
                        student = await cosmos.FindStudentByStnAsync(stn);
                    if (student is null && !string.IsNullOrWhiteSpace(localId))
                        student = await cosmos.FindStudentByLocalIdAsync(localId);
                    if (student is null && !string.IsNullOrWhiteSpace(name))
                    {
                        var (allStudents, _) = await cosmos.ListStudentsAsync(1, 1000, name, null);
                        student = allStudents.FirstOrDefault(s =>
                            s.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) && s.IsActive);
                    }

                    // IXL files can create students (they have full names and student IDs)
                    // Second LocalId check: if name search missed but same LocalId exists, use it
                    if (student is null && !string.IsNullOrWhiteSpace(localId))
                        student = await cosmos.FindStudentByLocalIdAsync(localId);

                    // Only auto-create for IXL classic format (has student name); LevelUp format has no name column
                    if (student is null && uploadType == "IXL" && !string.IsNullOrWhiteSpace(name))
                    {
                        var newId = $"s-{Guid.NewGuid():N}";
                        student = new StudentDocument
                        {
                            Id         = newId,
                            StudentId  = newId,
                            FullName   = name,
                            Stn        = stn,
                            LocalId    = localId,
                            ClassGroup = "Unassigned",
                            // IXL rows carry Grade/Gender too — flagged live during the 2026-08-14 client
                            // demo as "several students missing grade levels"; this branch previously
                            // dropped both even though the source row has them.
                            Grade      = GetVal(row, "Grade", "Grade_Level", "Enrolled Grade")?.TrimStart('0'),
                            Gender     = GetVal(row, "Gender"),
                            SourceFile = fileName,
                            Tier       = "Pending",
                            TierStatus = "Pending",
                            EnrolDate  = DateTime.UtcNow.ToString("o"),
                            LastUpdated = DateTime.UtcNow.ToString("o")
                        };
                        await cosmos.UpsertStudentAsync(student);
                    }

                    if (student is null) { skipped++; continue; }

                    // Assessment uploads only ever wrote an AssessmentDocument, never the matched
                    // student record — so a student who only ever receives assessment files (no
                    // further demographics re-upload) kept whatever SourceFile it had at creation,
                    // which is blank for older records. Track the most recent file that touched
                    // this student in any way so "Source File" reflects reality. See QA issue #11.
                    if (!string.Equals(student.SourceFile, fileName, StringComparison.Ordinal))
                    {
                        student.SourceFile = fileName;
                        await cosmos.UpsertStudentAsync(student);
                    }

                    var scoreRaw = GetVal(row, "Score", "Scale Score", "Overall Score",
                                         "Overall ELA score", "Overall math score", "Overall reading score",
                                         "Overall math scale score", "Overall ELA scale score",
                                         "Reading Composite Score", "Diagnostic level", "SmartScore");
                    double.TryParse(scoreRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var score);

                    var subject = GetVal(row, "Subject", "Content Area", "Test Subject")
                                  ?? DetectSubject(uploadType, fileName, row);
                    var rawProficiency = GetVal(row, "Proficiency Level", "Performance Level",
                                            "Status", "Achievement Level",
                                            "Overall math tier", "Overall ELA tier", "Overall reading tier",
                                            "Reading Composite Status");
                    var periodRaw = GetVal(row, "Period", "Term", "School Year",
                                        "Benchmark Period", "Test OppNumber");
                    var date = GetVal(row, "Date", "Date Taken", "Date of completion",
                                      "Test Date", "Reading Composite Date");

                    // Task 13: normalise I-Read pass/fail → standard proficiency labels
                    var proficiency = uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase)
                        ? NormalizeIReadProficiency(rawProficiency)
                        : rawProficiency;

                    // Task 12: normalise Acadience period (BOY/MOY/EOY)
                    // Task 11: normalise ILEARN period (CP1/CP2/CP3)
                    var period = uploadType.Equals("Acadience", StringComparison.OrdinalIgnoreCase)
                        ? NormalizeAcadiencePeriod(periodRaw, fileName)
                        : uploadType.Equals("ILEARN", StringComparison.OrdinalIgnoreCase)
                            ? NormalizeIlearnPeriod(periodRaw, fileName)
                            : periodRaw;

                    var normalizedSubject = NormalizeSubject(subject);
                    var finalScore = score > 0 ? (double?)score : null;

                    // A test event is identified by student + type + subject + date + score.
                    // FileName is deliberately excluded: the same result arriving under two
                    // different filenames is still one event, not two.
                    if (!seenAssessments.TryGetValue(student.StudentId, out var signatures))
                    {
                        var existing = await cosmos.GetAssessmentsAsync(student.StudentId);
                        signatures = existing
                            .Select(a => AssessmentSignature(a.UploadType, a.Subject, a.Date, a.Score))
                            .ToHashSet(StringComparer.Ordinal);
                        seenAssessments[student.StudentId] = signatures;
                    }

                    var signature = AssessmentSignature(uploadType, normalizedSubject, date, finalScore);
                    if (!signatures.Add(signature))
                    {
                        duplicateAssessments++;
                        continue;
                    }

                    await cosmos.CreateAssessmentAsync(new AssessmentDocument
                    {
                        Id         = Guid.NewGuid().ToString(),
                        StudentId  = student.StudentId,
                        UploadType = uploadType,
                        FileName   = fileName,
                        UploadedAt = DateTime.UtcNow.ToString("o"),
                        Subject    = normalizedSubject,
                        Score      = finalScore,
                        Proficiency = proficiency,
                        Period     = period,
                        Date       = date,
                        RawFields  = piiRedaction.RedactRawFields(row)
                    });
                }

                imported++;
            }
            catch (Exception ex)
            {
                errors.Add($"Row error: {ex.Message}");
                skipped++;
            }
        }

        return new ParseSummaryDto(rows.Count, imported, skipped, errors, duplicateAssessments);
    }

    private static string DetectSubject(string uploadType, string fileName,
                                        Dictionary<string, string>? row = null)
    {
        // Acadience and I-Read are always Reading — must check before generic ELA/Reading check
        if (uploadType.Equals("Acadience", StringComparison.OrdinalIgnoreCase) ||
            uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Acadience", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("IREAD", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("I-READ", StringComparison.OrdinalIgnoreCase)) return "Reading";

        if (uploadType.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("EnglishLanguageArts", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (uploadType.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Mathematics", StringComparison.OrdinalIgnoreCase)) return "Math";

        // Filename gave no signal. Combined IXL exports (e.g. "IXL-LevelUp-Diagnostic-Results-*.csv")
        // contain neither "ELA" nor "Math" in the name, so rows from them were previously stored as
        // "Mixed" — which the tier engine and dashboard subject grouping both ignore, silently
        // discarding valid scores. The columns themselves are unambiguous, so fall back to those.
        if (row is not null)
        {
            var hasEla  = row.Keys.Any(k => k.Contains("Overall ELA", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("Overall reading", StringComparison.OrdinalIgnoreCase)
                                         || k.Contains("Overall writing", StringComparison.OrdinalIgnoreCase));
            var hasMath = row.Keys.Any(k => k.Contains("Overall math", StringComparison.OrdinalIgnoreCase));
            if (hasEla && !hasMath) return "ELA";
            if (hasMath && !hasEla) return "Math";
        }

        // Genuinely ambiguous — tier engine will treat as unknown.
        return "Mixed";
    }

    private static string NormalizeSubject(string? subject)
    {
        if (subject is null) return "Mixed";
        if (subject.Equals("Reading", StringComparison.OrdinalIgnoreCase)) return "Reading";
        if (subject.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (subject.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        return subject;
    }

    // Maps I-Read pass/fail status strings → standard proficiency labels (BRD task 13)
    private static string? NormalizeIReadProficiency(string? raw)
    {
        if (raw is null) return null;
        var v = raw.Trim();
        if (v.Contains("Did Not Pass", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Fail", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("F", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Not Passed", StringComparison.OrdinalIgnoreCase)) return "Below Proficiency";
        if (v.Equals("Passed", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("Pass", StringComparison.OrdinalIgnoreCase) ||
            v.Equals("P", StringComparison.OrdinalIgnoreCase)) return "At Proficiency";
        if (v.Contains("Waived", StringComparison.OrdinalIgnoreCase)) return "Waived";
        if (v.Contains("Exempt", StringComparison.OrdinalIgnoreCase)) return "Exempt";
        return raw; // preserve unrecognised values as-is
    }

    // Extracts BOY/MOY/EOY period tag from Acadience filename or Period column value
    private static string? NormalizeAcadiencePeriod(string? periodCol, string fileName)
    {
        var candidates = new[] { periodCol ?? "", fileName };
        foreach (var s in candidates)
        {
            if (s.Contains("BOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Beginning", StringComparison.OrdinalIgnoreCase)) return "BOY";
            if (s.Contains("MOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Middle", StringComparison.OrdinalIgnoreCase)) return "MOY";
            if (s.Contains("EOY", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("End", StringComparison.OrdinalIgnoreCase)) return "EOY";
        }
        return periodCol; // fall back to whatever is in the column
    }

    // Extracts ILEARN checkpoint number from filename (CP1, CP2, CP3, Checkpoint1…)
    private static string? NormalizeIlearnPeriod(string? periodCol, string fileName)
    {
        var candidates = new[] { periodCol ?? "", fileName };
        foreach (var s in candidates)
        {
            if (s.Contains("CP1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint1", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 1", StringComparison.OrdinalIgnoreCase)) return "CP1";
            if (s.Contains("CP2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint2", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 2", StringComparison.OrdinalIgnoreCase)) return "CP2";
            if (s.Contains("CP3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint3", StringComparison.OrdinalIgnoreCase) ||
                s.Contains("Checkpoint 3", StringComparison.OrdinalIgnoreCase)) return "CP3";
        }
        return periodCol;
    }

    // Indiana STNs are 9 characters: either all digits, or a single letter prefix followed by
    // 8 digits (T/N/C/E prefixes observed in LGS files). IXL's internal student IDs are 7 digits
    // and must not be mistaken for an STN — see BRD v1.2 DI-12.
    private static bool LooksLikeStn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        if (v.Length != 9) return false;
        if (v.All(char.IsDigit)) return true;
        return char.IsLetter(v[0]) && v.Skip(1).All(char.IsDigit);
    }

    private static string? GetVal(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val)) return val.Trim();
            var match = row.Keys.FirstOrDefault(k =>
                k.EndsWith("." + key, StringComparison.OrdinalIgnoreCase) ||
                k.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(row[match])) return row[match].Trim();
        }
        return null;
    }

    // Exact-match only — no fuzzy substring. Use for name columns to avoid picking up STUDENTS.ID etc.
    private static string? GetValExact(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(key, out var val) && !string.IsNullOrWhiteSpace(val)) return val.Trim();
        }
        return null;
    }

    private static async Task<List<Dictionary<string, string>>> ParseCsvAsync(IFormFile file, CancellationToken ct)
    {
        var config = new CsvHelper.Configuration.CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            MissingFieldFound = null,
            BadDataFound = null,
            TrimOptions = CsvHelper.Configuration.TrimOptions.Trim,
        };
        using var reader = new StreamReader(file.OpenReadStream(), detectEncodingFromByteOrderMarks: true);
        using var csv = new CsvReader(reader, config);
        var rows = new List<Dictionary<string, string>>();
        await csv.ReadAsync();
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];
        while (await csv.ReadAsync())
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < headers.Length; i++)
            {
                var header = headers[i].Trim().TrimStart('﻿');
                if (string.IsNullOrWhiteSpace(header)) continue;
                if (!row.ContainsKey(header))
                    row[header] = csv.GetField(i) ?? "";
            }
            rows.Add(row);
        }
        return rows;
    }

    private static List<Dictionary<string, string>> ParseXlsx(IFormFile file)
    {
        using var package = new ExcelPackage(file.OpenReadStream());
        var sheet = package.Workbook.Worksheets[0];
        var rows = new List<Dictionary<string, string>>();
        if (sheet.Dimension is null) return rows;

        var headers = Enumerable.Range(1, sheet.Dimension.Columns)
            .Select(c => sheet.Cells[1, c].Text.Trim()).ToArray();

        for (int r = 2; r <= sheet.Dimension.Rows; r++)
        {
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int c = 1; c <= headers.Length; c++)
                row[headers[c - 1]] = sheet.Cells[r, c].Text.Trim();
            rows.Add(row);
        }
        return rows;
    }

    public record ParseSummaryDto(int TotalRows, int ImportedRows, int SkippedRows, List<string> Errors,
                                  int DuplicateAssessments = 0);

    // Identifies a single test event. Two rows sharing this signature are the same result,
    // regardless of which file they arrived in.
    private static string AssessmentSignature(string? uploadType, string? subject, string? date, double? score)
        => string.Join('|',
            (uploadType ?? "").Trim().ToLowerInvariant(),
            (subject ?? "").Trim().ToLowerInvariant(),
            (date ?? "").Trim(),
            score?.ToString(CultureInfo.InvariantCulture) ?? "");

    // ─── Validation helpers ───────────────────────────────────────────────────

    private static string ComputeSha256(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // Required columns per upload type — at least one column from each group must be present.
    // Groups are OR-ed within themselves; all groups are AND-ed across.
    private static readonly Dictionary<string, List<string[]>> RequiredColumnGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        ["demographics"] = [
            ["Student_Number", "STN", "State_StudentNumber", "Local Student ID", "STUDENTS.Student_Number"],
            ["Last name", "Last Name", "FullName", "Full Name", "Student Legal Name", "Legal Last Name", "Student Legal Last Name"],
        ],
        ["ILEARN"] = [
            ["STN", "State_StudentNumber", "Student State ID", "ILEARN Student ID", "Statewide Student ID"],
            ["Score", "Scale Score", "Performance Level", "Achievement Level"],
        ],
        ["IXL"] = [
            // LevelUp Benchmark exports carry a real STN column; older Diagnostic exports use "ID".
            ["Student_Number", "ID", "Local ID", "Local Student Number", "STN", "State_StudentNumber"],
            ["SmartScore", "Score", "Diagnostic level", "Overall math score", "Overall ELA score", "Overall math tier", "Overall ELA tier"],
        ],
        ["Acadience"] = [
            ["STN", "Student State ID", "State_StudentNumber"],
            ["Reading Composite Score", "Score", "Scale Score"],
        ],
        ["IREAD"] = [
            ["STN", "State_StudentNumber", "Student State ID"],
            ["Status", "Proficiency Level", "Achievement Level"],
        ],
    };

    // Signature columns that strongly indicate a given upload type.
    // Used to detect mismatches (e.g. uploading an ILEARN file as demographics).
    private static readonly Dictionary<string, string[]> TypeSignatureColumns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["demographics"] = ["Grade_Level", "Home_Room", "STUDENTS.DOB", "Ethnicity", "ELL", "LunchStatus", "STUDENTS.Student_Number"],
        ["ILEARN"] = ["Scale Score", "Achievement Level", "ILEARN Student ID", "Performance Level"],
        // "Date of completion" alone tied with demographics/ILEARN on LevelUp Benchmark files and lost
        // the tie-break, so the IXL-only "Overall <subject> tier/scale score" columns are included too.
        // Entries are matched against column headers, not the filename — see DetectUploadType for that.
        ["IXL"] = ["SmartScore", "Diagnostic level", "Date of completion",
                   "Overall math tier", "Overall ELA tier", "Overall math scale score", "Overall reading scale score"],
        ["Acadience"] = ["Reading Composite Score", "Reading Composite Status", "Reading Composite Date", "ALO"],
        ["IREAD"] = ["I-Read", "IREAD", "Did Not Pass", "Passed", "Reading Grade Level"],
    };

    // Returns a validation error message, or null if valid.
    private static string? ValidateSchema(IReadOnlyList<string> headers, string uploadType)
    {
        if (!RequiredColumnGroups.TryGetValue(uploadType, out var groups)) return null;

        var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var found = group.Any(col =>
                headerSet.Contains(col) ||
                headers.Any(h => h.Contains(col, StringComparison.OrdinalIgnoreCase)));
            if (!found)
                return $"Missing required column — expected one of: {string.Join(", ", group)}";
        }
        return null;
    }

    // Returns the most-likely upload type based on column signatures, or null if ambiguous/unknown.
    private static string? DetectTypeFromColumns(IReadOnlyList<string> headers)
    {
        var headerSet = new HashSet<string>(headers, StringComparer.OrdinalIgnoreCase);
        string? best = null;
        int bestScore = 0;
        foreach (var (type, sigs) in TypeSignatureColumns)
        {
            int score = sigs.Count(col =>
                headerSet.Contains(col) ||
                headers.Any(h => h.Contains(col, StringComparison.OrdinalIgnoreCase)));
            if (score > bestScore) { bestScore = score; best = type; }
        }
        return bestScore >= 1 ? best : null;
    }
}
