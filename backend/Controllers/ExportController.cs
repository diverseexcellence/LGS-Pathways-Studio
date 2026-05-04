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
        ws.Cells[1, 1, 1, 10].Merge = true;
        ws.Cells[1, 1].Style.Font.Bold = true;
        ws.Cells[1, 1].Style.Font.Color.SetColor(Color.White);
        ws.Cells[1, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[1, 1].Style.Fill.BackgroundColor.SetColor(Color.DarkRed);
        ws.Cells[1, 1].Style.HorizontalAlignment = OfficeOpenXml.Style.ExcelHorizontalAlignment.Center;

        // Row 2 — watermark: who exported and when
        ws.Cells[2, 1].Value = $"Exported by: {adminName} ({CurrentAdminEmail})  |  Date: {exportedAt:yyyy-MM-dd HH:mm} UTC  |  Records: {students.Count}";
        ws.Cells[2, 1, 2, 10].Merge = true;
        ws.Cells[2, 1].Style.Font.Italic = true;
        ws.Cells[2, 1].Style.Font.Color.SetColor(Color.DarkRed);
        ws.Cells[2, 1].Style.Fill.PatternType = ExcelFillStyle.Solid;
        ws.Cells[2, 1].Style.Fill.BackgroundColor.SetColor(Color.LightYellow);

        string[] headers = ["ID", "Full Name", "DOB", "Class", "Grade", "Gender", "Ethnicity", "ELL", "Tier", "Tier Status"];
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
            ws.Cells[row, 9].Value  = s.Tier;
            ws.Cells[row, 10].Value = s.TierStatus;

            if (r % 2 == 1)
            {
                ws.Cells[row, 1, row, 10].Style.Fill.PatternType = ExcelFillStyle.Solid;
                ws.Cells[row, 1, row, 10].Style.Fill.BackgroundColor.SetColor(Color.FromArgb(0xF4, 0xF4, 0xF4));
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
