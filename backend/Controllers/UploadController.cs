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
public class UploadController(ICosmosDbService cosmos, IBlobStorageService blob, IAuditService audit, IPiiRedactionService piiRedaction) : ControllerBase
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

    private sealed class StreamFormFile(Stream stream, string fileName, string contentType) : IFormFile
    {
        public string ContentType => contentType;
        public string ContentDisposition => $"form-data; name=\"file\"; filename=\"{fileName}\"";
        public IHeaderDictionary Headers => new HeaderDictionary();
        public long Length => stream.Length;
        public string Name => "file";
        public string FileName => fileName;
        public void CopyTo(Stream target) => stream.CopyTo(target);
        public Task CopyToAsync(Stream target, CancellationToken ct = default) => stream.CopyToAsync(target, ct);
        public Stream OpenReadStream() => stream;
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
                var name = GetVal(row, "FullName", "Full Name", "Name", "Student Name");
                if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

                if (uploadType == "demographics")
                {
                    var dob = GetVal(row, "DOB", "Date of Birth", "Birth Date") ?? "";
                    var existing = await cosmos.FindStudentByNameAndDobAsync(name, dob);
                    if (existing is not null) { skipped++; continue; }

                    var studentId = $"s-{Guid.NewGuid():N}";
                    await cosmos.UpsertStudentAsync(new StudentDocument
                    {
                        Id         = studentId,
                        StudentId  = studentId,
                        FullName   = name,
                        Dob        = dob,
                        ClassGroup = GetVal(row, "Class", "ClassGroup", "Class Group", "Home_Room", "Homeroom") ?? "Unassigned",
                        Grade      = GetVal(row, "Grade", "Grade_Level", "Enrolled Grade")?.TrimStart('0'),
                        Gender     = GetVal(row, "Gender"),
                        Ethnicity  = GetVal(row, "Ethnicity", "Race"),
                        EllStatus  = GetVal(row, "ELL", "English Learner"),
                        SpedStatus = GetVal(row, "SPED", "Special Education"),
                        Section504 = GetVal(row, "504"),
                        HomeRoom   = GetVal(row, "HomeRoom", "Home Room", "Home_Room"),
                        SourceFile = fileName,
                        Tier       = "Pending",
                        TierStatus = "Pending",
                        EnrolDate  = DateTime.UtcNow.ToString("o"),
                        LastUpdated = DateTime.UtcNow.ToString("o")
                    });
                }
                else
                {
                    // Assessment — match student by name
                    var (allStudents, _) = await cosmos.ListStudentsAsync(1, 1000, name, null);
                    var student = allStudents.FirstOrDefault(s =>
                        s.FullName.Equals(name, StringComparison.OrdinalIgnoreCase) && s.IsActive);

                    if (student is null) { skipped++; continue; }

                    var scoreRaw = GetVal(row, "Score", "Scale Score", "Overall Score", "Diagnostic level", "SmartScore");
                    double.TryParse(scoreRaw, out var score);

                    var subject = GetVal(row, "Subject", "Content Area") ?? DetectSubject(uploadType, fileName);
                    var proficiency = GetVal(row, "Proficiency Level", "Performance Level", "Status", "Achievement Level");
                    var period = GetVal(row, "Period", "Term", "School Year");
                    var date = GetVal(row, "Date", "Date Taken", "Date of completion", "Test Date");

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
