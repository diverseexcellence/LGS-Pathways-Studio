using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using CsvHelper;
using System.Globalization;
using OfficeOpenXml;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/upload")]
[Authorize]
public class UploadController(ICosmosDbService cosmos, IBlobStorageService blob, IAuditService audit, IPiiRedactionService piiRedaction, ISchoolAverageService schoolAverages) : ControllerBase
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

        List<Dictionary<string, string>> rows;
        try { rows = ext == ".csv" ? await ParseCsvAsync(file, ct) : ParseXlsx(file); }
        catch (Exception ex) { return BadRequest(new { message = $"Parse error: {ex.Message}" }); }

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
            Id          = Guid.NewGuid().ToString(),
            UploadedBy  = CurrentAdminEmail,
            FileName    = file.FileName,
            UploadType  = uploadType,
            UploadedAt  = DateTime.UtcNow.ToString("o"),
            RecordCount = result.ImportedRows,
            SkippedCount = result.SkippedRows,
            Errors      = result.Errors,
            BlobUrl     = blobUrl
        });

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Upload, entityType: "Upload", entityId: file.FileName,
            details: $"Uploaded {file.FileName} ({uploadType}) — {result.ImportedRows} rows, {result.SkippedRows} skipped",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        // Refresh cached school averages after any assessment upload (fire-and-forget, non-blocking)
        _ = Task.Run(() => schoolAverages.RefreshAsync());

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
                var formFile = new StreamFormFile(stream, name,
                    ext == ".csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                rows = ext == ".csv" ? await ParseCsvAsync(formFile, ct) : ParseXlsx(formFile);

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

        return Ok(new { message = $"Processed {files.Count} file(s) from landing-zone.", results });
    }

    private static string DetectUploadType(string fileName)
    {
        var n = fileName.ToUpperInvariant();
        if (n.StartsWith("TEST_")) return "__SKIP__";
        if (n.Contains("ILEARN") || n.Contains("CHECKPOINT")) return "ILEARN";
        if (n.Contains("IXL")) return "IXL";
        if (n.Contains("ACADIENCE")) return "Acadience";
        if (n.Contains("IREAD") || n.Contains("I-READ")) return "IREAD";
        if (n.Contains("ALO") || n.Contains("READING_PM") || n.Contains("READING-PM")) return "Acadience";
        return "demographics";
    }

    private sealed class StreamFormFile : IFormFile
    {
        private readonly Stream _stream;
        private readonly string _fileName;
        private readonly string _contentType;

        public StreamFormFile(Stream stream, string fileName, string contentType)
        {
            _stream = stream;
            _fileName = fileName;
            _contentType = contentType;
        }

        public string ContentType => _contentType;
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{_fileName}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => _stream.Length;
        public string Name => "file";
        public string FileName => _fileName;
        public void CopyTo(Stream target) => _stream.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => _stream.CopyToAsync(target, ct);
        public Stream OpenReadStream() => _stream;
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
        int imported = 0, skipped = 0;
        var errors = new List<string>();

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
                                     "Student ID", "ILEARN Student ID", "Statewide Student ID");
                    var localId = GetVal(row, "ID", "Student_Number", "Student Number",
                                         "Local ID", "Local Student ID", "School ID",
                                         "Local Student Number");
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

                    if (student is null && uploadType == "IXL" && !string.IsNullOrWhiteSpace(name))
                    {
                        var newId = $"s-{Guid.NewGuid():N}";
                        student = new StudentDocument
                        {
                            Id         = newId,
                            StudentId  = newId,
                            FullName   = name,
                            LocalId    = localId,
                            ClassGroup = "Unassigned",
                            SourceFile = fileName,
                            Tier       = "Pending",
                            TierStatus = "Pending",
                            EnrolDate  = DateTime.UtcNow.ToString("o"),
                            LastUpdated = DateTime.UtcNow.ToString("o")
                        };
                        await cosmos.UpsertStudentAsync(student);
                    }

                    if (student is null) { skipped++; continue; }

                    var scoreRaw = GetVal(row, "Score", "Scale Score", "Overall Score",
                                         "Overall ELA score", "Overall reading score",
                                         "Reading Composite Score", "Diagnostic level", "SmartScore");
                    double.TryParse(scoreRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var score);

                    var subject = GetVal(row, "Subject", "Content Area", "Test Subject")
                                  ?? DetectSubject(uploadType, fileName);
                    var proficiency = GetVal(row, "Proficiency Level", "Performance Level",
                                            "Status", "Achievement Level", "Overall ELA tier",
                                            "Reading Composite Status");
                    var period = GetVal(row, "Period", "Term", "School Year",
                                        "Benchmark Period", "Test OppNumber");
                    var date = GetVal(row, "Date", "Date Taken", "Date of completion",
                                      "Test Date", "Reading Composite Date");

                    await cosmos.CreateAssessmentAsync(new AssessmentDocument
                    {
                        Id         = Guid.NewGuid().ToString(),
                        StudentId  = student.StudentId,
                        UploadType = uploadType,
                        FileName   = fileName,
                        UploadedAt = DateTime.UtcNow.ToString("o"),
                        Subject    = NormalizeSubject(subject),
                        Score      = score > 0 ? score : null,
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

        return new ParseSummaryDto(rows.Count, imported, skipped, errors);
    }

    private static string DetectSubject(string uploadType, string fileName)
    {
        if (uploadType.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("EnglishLanguageArts", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Language", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Reading", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (uploadType.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Math", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Mathematics", StringComparison.OrdinalIgnoreCase)) return "Math";
        return "Mixed";
    }

    private static string NormalizeSubject(string? subject)
    {
        if (subject is null) return "Mixed";
        if (subject.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            subject.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (subject.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        return subject;
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

    public record ParseSummaryDto(int TotalRows, int ImportedRows, int SkippedRows, List<string> Errors);
}
