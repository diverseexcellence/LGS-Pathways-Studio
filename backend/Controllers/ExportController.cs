using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using System.Drawing;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/export")]
[Authorize]
public class ExportController(ICosmosDbService cosmos, IBlobStorageService blob, IAuditService audit) : ControllerBase
{
    private int CurrentAdminId => int.Parse(User.FindFirstValue("adminId") ?? "0");
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";

    /// <summary>
    /// BRD DI-10: return unmatched STN rows as JSON for the in-app viewer.
    /// </summary>
    [HttpGet("unmatched-stns/list")]
    public async Task<IActionResult> UnmatchedStnsList(CancellationToken ct)
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
        var knownStns = students
            .Where(s => !string.IsNullOrWhiteSpace(s.Stn))
            .Select(s => s.Stn!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allAssessments = await cosmos.GetAllAssessmentsAsync();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<object>();

        foreach (var a in allAssessments)
        {
            var rawStn = a.RawFields
                .Where(kv => kv.Key.Contains("STN", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains("SSID", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value?.Trim())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            if (string.IsNullOrWhiteSpace(rawStn)) continue;
            if (knownStns.Contains(rawStn)) continue;

            var key = $"{rawStn}|{a.FileName}";
            if (!seen.Add(key)) continue;

            rows.Add(new { stn = rawStn, uploadType = a.UploadType, fileName = a.FileName, uploadedAt = a.UploadedAt });
        }

        return Ok(new { total = rows.Count, rows = rows.OrderBy(r => ((dynamic)r).stn) });
    }

    /// <summary>
    /// BRD DI-10: diff assessment STNs against demographic STNs and return a CSV of unmatched rows.
    /// Rows = assessment records whose STN has no corresponding active student in the students container.
    /// </summary>
    [HttpGet("unmatched-stns")]
    public async Task<IActionResult> UnmatchedStns(CancellationToken ct)
    {
        // Load all active student STNs into a set for O(1) lookup
        var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
        var knownStns = students
            .Where(s => !string.IsNullOrWhiteSpace(s.Stn))
            .Select(s => s.Stn!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Load all assessment records
        var allAssessments = await cosmos.GetAllAssessmentsAsync();

        // Find assessments whose studentId maps to a student with no STN, or where the raw STN
        // stored in RawFields doesn't match any known student STN
        var studentById = students.ToDictionary(s => s.StudentId, StringComparer.OrdinalIgnoreCase);

        var unmatched = new List<(string Stn, string StudentId, string UploadType, string FileName, string UploadedAt)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var a in allAssessments)
        {
            // Extract the raw STN stored in the assessment's RawFields
            var rawStn = a.RawFields
                .Where(kv => kv.Key.Contains("STN", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains("State", StringComparison.OrdinalIgnoreCase) ||
                             kv.Key.Contains("SSID", StringComparison.OrdinalIgnoreCase))
                .Select(kv => kv.Value?.Trim())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

            if (string.IsNullOrWhiteSpace(rawStn)) continue;
            if (knownStns.Contains(rawStn)) continue;

            // Dedupe per STN + upload file combination
            var key = $"{rawStn}|{a.FileName}";
            if (!seen.Add(key)) continue;

            unmatched.Add((rawStn, a.StudentId, a.UploadType, a.FileName, a.UploadedAt));
        }

        // Build CSV
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("STN,StudentId,UploadType,FileName,UploadedAt");
        foreach (var (stn, studentId, uploadType, fileName, uploadedAt) in unmatched.OrderBy(r => r.Stn))
            sb.AppendLine($"{CsvEscape(stn)},{CsvEscape(studentId)},{CsvEscape(uploadType)},{CsvEscape(fileName)},{CsvEscape(uploadedAt)}");

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Export, entityType: "DataQuality",
            details: $"Unmatched STN report: {unmatched.Count} unmatched assessment rows across {unmatched.Select(r => r.FileName).Distinct().Count()} file(s)",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        var csvBytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        var reportName = $"unmatched-stns-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return File(csvBytes, "text/csv", reportName);
    }

    private static string CsvEscape(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    [HttpGet]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        ExcelPackage.LicenseContext = LicenseContext.NonCommercial;

        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null);
        var adminName = User.FindFirstValue("name") ?? CurrentAdminEmail;
        var exportedAt = DateTime.UtcNow;

        using var package = new ExcelPackage();
        var ws = package.Workbook.Worksheets.Add("Students");

        // Row 1 — confidentiality banner
        ws.Cells[1, 1].Value = "CONFIDENTIAL — FOR AUTHORISED LGS STAFF USE ONLY — DO NOT DISTRIBUTE";
        ws.Cells[1, 1, 1, 16].Merge = true;
        ws.Cells[1, 1].Style.Font.Bold = true;
        ws.Cells[1, 1].Style.Font.Color.SetColor(Color.White);
        ws.Cells[1, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[1, 1].Style.Fill.BackgroundColor.SetColor(Color.DarkRed);
        ws.Cells[1, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;

        // Row 2 — watermark: who exported and when
        ws.Cells[2, 1].Value = $"Exported by: {adminName} ({CurrentAdminEmail})  |  Date: {exportedAt:yyyy-MM-dd HH:mm} UTC  |  Records: {students.Count}";
        ws.Cells[2, 1, 2, 16].Merge = true;
        ws.Cells[2, 1].Style.Font.Italic = true;
        ws.Cells[2, 1].Style.Font.Color.SetColor(Color.DarkRed);
        ws.Cells[2, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[2, 1].Style.Fill.BackgroundColor.SetColor(Color.LightYellow);

        string[] headers = ["ID", "Full Name", "DOB", "Class", "Grade", "Gender", "Ethnicity", "ELL",
                            "ELA Tier", "ELA Status", "ELA Score", "ELA Data Pts",
                            "Math Tier", "Math Status", "Math Score", "Math Data Pts"];
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cells[3, i + 1].Value = headers[i];
            ws.Cells[3, i + 1].Style.Font.Bold = true;
            ws.Cells[3, i + 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
            ws.Cells[3, i + 1].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(0x21, 0x49, 0x65));
            ws.Cells[3, i + 1].Style.Font.Color.SetColor(Color.White);
        }

        for (int r = 0; r < students.Count; r++)
        {
            var s = students[r];
            int row = r + 4;
            ws.Cells[row, 1].Value  = s.StudentId;
            ws.Cells[row, 2].Value  = s.FullName;
            ws.Cells[row, 3].Value  = s.Dob;
            ws.Cells[row, 4].Value  = s.ClassGroup;
            ws.Cells[row, 5].Value  = s.Grade;
            ws.Cells[row, 6].Value  = s.Gender;
            ws.Cells[row, 7].Value  = s.Ethnicity;
            ws.Cells[row, 8].Value  = s.EllStatus;
            ws.Cells[row, 9].Value  = s.ElaTier.Tier ?? "Pending";
            ws.Cells[row, 10].Value = s.ElaTier.Status;
            ws.Cells[row, 11].Value = s.ElaTier.Score;
            ws.Cells[row, 12].Value = s.ElaTier.DataPoints;
            ws.Cells[row, 13].Value = s.MathTier.Tier ?? "Pending";
            ws.Cells[row, 14].Value = s.MathTier.Status;
            ws.Cells[row, 15].Value = s.MathTier.Score;
            ws.Cells[row, 16].Value = s.MathTier.DataPoints;

            if (r % 2 == 1)
            {
                ws.Cells[row, 1, row, 16].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, 1, row, 16].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(0xF4, 0xF4, 0xF4));
            }
        }

        ws.Cells[ws.Dimension?.Address ?? "A1"].AutoFitColumns();

        var bytes = package.GetAsByteArray();
        var fileName = $"lgs-students-{DateTime.UtcNow:yyyyMMdd-HHmm}.xlsx";

        string? blobUrl = null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var blobName = await blob.UploadAsync(ms, fileName, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ct);
            blobUrl = await blob.GetSasUrlAsync(blobName);
        }
        catch { }

        await cosmos.CreateExportLogAsync(new ExportLogDocument
        {
            Id          = Guid.NewGuid().ToString(),
            ExportedBy  = CurrentAdminEmail,
            FileName    = fileName,
            ExportedAt  = DateTime.UtcNow.ToString("o"),
            RecordCount = students.Count,
            BlobUrl     = blobUrl
        });

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Export, entityType: "Export",
            details: $"Exported {students.Count} students → {fileName}",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            fileName);
    }
}
