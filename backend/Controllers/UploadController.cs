using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Security.Cryptography;
using CsvHelper;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OfficeOpenXml;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/upload")]
[Authorize]
public class UploadController(ICosmosDbService cosmos, IBlobStorageService blob, IAuditService audit, IPiiRedactionService piiRedaction, ISchoolAverageService schoolAverages, ITierCalculationService tierCalculation, ILogger<UploadController> logger, ILandingZoneImportStatusService importStatus, IConfiguration configuration) : ControllerBase
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

        // An upload type with no schema, no column signature and no row handling cannot import
        // anything, but every stage downstream treats it as valid: ValidateSchema returns null for
        // an unknown type, so the file parsed, uploaded and logged, then every row was skipped and
        // the response was a 200 that looked like a successful import of zero rows. Reject it here
        // instead — this covers a typo, a stale client, and a type added to the picker before the
        // backend supports it.
        if (!SupportedUploadTypes.Contains(uploadType))
            return BadRequest(new
            {
                message = $"Unsupported data type \"{uploadType}\". Choose one of: " +
                          $"{string.Join(", ", SupportedUploadTypes.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))}."
            });

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
        // (demographics adds new students; assessments change available signals). Batched: fetches
        // all assessments and the ruleset once instead of once per student, and per-subject
        // Finalized gating happens inside the engine, so a student with one Finalized subject still
        // gets the other subject recalculated.
        _ = Task.Run(async () =>
        {
            try
            {
                var (students, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null, activeOnly: true);
                var updated = await tierCalculation.ComputeAndApplyBatchAsync(students.Where(s => !s.AllSubjectsOverridden).ToList());
                logger.LogInformation("Post-upload tier recalculation updated {Count} of {Total} students", updated, students.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Post-upload tier recalculation failed");
            }
        });

        return Ok(result);
    }

    // Runs the whole batch in the background and returns immediately. This used to block the HTTP
    // request for every file in the container — with a couple dozen files and per-row Cosmos
    // lookups for student matching, that routinely exceeded Azure App Service's ~230s platform
    // request timeout. The connection got reset mid-response (browsers see this as "Unexpected end
    // of JSON input"), even though the import kept running to completion server-side regardless.
    // Poll GET import-landing-zone/status for progress and the final result.
    [HttpPost("import-landing-zone")]
    public IActionResult ImportLandingZone([FromQuery] string? only)
    {
        if (!importStatus.TryStart())
            return Conflict(new { message = "A landing-zone import is already running. Poll GET /api/upload/import-landing-zone/status." });

        var adminId = CurrentAdminId;
        var adminEmail = CurrentAdminEmail;
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        // Deliberately not tied to HttpContext.RequestAborted — the whole point is that this must
        // keep running after the request that started it has ended.
        _ = Task.Run(() => RunLandingZoneImportAsync(adminId, adminEmail, ip, only));

        return Accepted(new { message = "Landing-zone import started in the background.", status = "running" });
    }

    [HttpGet("import-landing-zone/status")]
    public IActionResult ImportLandingZoneStatus() => Ok(importStatus.Current);

    private async Task RunLandingZoneImportAsync(int adminId, string adminEmail, string? ip, string? only)
    {
        try { await RunLandingZoneImportCoreAsync(adminId, adminEmail, ip, only); }
        catch (Exception ex)
        {
            logger.LogError(ex, "Landing-zone import crashed");
            importStatus.Fail(ex.Message);
        }
    }

    private async Task RunLandingZoneImportCoreAsync(int adminId, string adminEmail, string? ip, string? only)
    {
        List<LandingZoneFile> files;
        try { files = await blob.ListLandingZoneFilesAsync(CancellationToken.None); }
        catch (Exception ex) { importStatus.Fail($"Could not access landing-zone: {ex.Message}"); return; }

        if (files.Count == 0) { importStatus.Complete("No CSV or Excel files found in landing-zone.", new List<object>()); return; }

        var results = new List<object>();
        foreach (var file in files)
        {
            var ct = CancellationToken.None;
            try
            {
                var name = file.Name;
                var stream = file.Content;
                var ext = Path.GetExtension(name).ToLowerInvariant();
                var uploadType = DetectUploadType(name);

                if (!IncludeLandingZoneFile(name, only))
                {
                    results.Add(new { file = name, uploadType = "filtered", result = (object?)null,
                                      error = "Skipped by import filter." });
                    await stream.DisposeAsync();
                    continue;
                }

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
                    UploadedBy   = adminEmail,
                    FileName     = name,
                    UploadType   = uploadType,
                    UploadedAt   = DateTime.UtcNow.ToString("o"),
                    RecordCount  = result.ImportedRows,
                    SkippedCount = result.SkippedRows,
                    ContentHash  = contentHash,
                    Errors       = result.Errors
                });

                await audit.LogAsync(adminId, adminEmail,
                    AuditEventType.Upload, entityType: "Upload", entityId: name,
                    details: $"Landing zone import: {name} ({uploadType}) — {result.ImportedRows} rows, {result.SkippedRows} skipped",
                    ip: ip);

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

        // Refresh school averages and recompute tiers for all students with at least one
        // non-Finalized subject. Awaited (not fire-and-forget) — this whole method already runs
        // off the request thread, so there's no HTTP timeout left to race against, and awaiting
        // means the "completed" status below only fires once tiers actually reflect the import.
        try { await schoolAverages.RefreshAsync(); }
        catch (Exception ex) { logger.LogError(ex, "School average refresh failed after landing-zone import"); }

        try
        {
            var (students, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null, activeOnly: true);
            var updated = await tierCalculation.ComputeAndApplyBatchAsync(students.Where(s => !s.AllSubjectsOverridden).ToList());
            logger.LogInformation("Landing-zone tier recalculation updated {Count} of {Total} students", updated, students.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Landing-zone tier recalculation failed");
        }

        importStatus.Complete($"Processed {files.Count} file(s) from landing-zone.", results);
    }

    /// <summary>
    /// Optional landing-zone filter. <c>only=cp1-cp2</c> imports ILEARN Checkpoint 1 and 2
    /// files only (Checkpoint 3, IXL, Acadience, and PowerSchool are left untouched).
    /// </summary>
    internal static bool IncludeLandingZoneFile(string fileName, string? only)
    {
        if (string.IsNullOrWhiteSpace(only)) return true;
        if (!only.Equals("cp1-cp2", StringComparison.OrdinalIgnoreCase)) return true;

        var n = fileName;
        if (n.Contains("Checkpoint3", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("Checkpoint 3", StringComparison.OrdinalIgnoreCase) ||
            n.Contains("CP3", StringComparison.OrdinalIgnoreCase))
            return false;

        return n.Contains("Checkpoint1", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Checkpoint 1", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Checkpoint2", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("Checkpoint 2", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("CP1", StringComparison.OrdinalIgnoreCase) ||
               n.Contains("CP2", StringComparison.OrdinalIgnoreCase);
    }

    internal static string DetectUploadType(string fileName)
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
        var eligible = students.Where(s => !s.AllSubjectsOverridden).ToList();
        var updated = await tierCalculation.ComputeAndApplyBatchAsync(eligible);
        return Ok(new { message = "Tier recalculation complete.", processed = eligible.Count, updated });
    }

    // ── Data quality: why is a student Pending? Also the migration decision tool after re-upload. ──
    [HttpGet("tier-data-quality")]
    public async Task<IActionResult> TierDataQuality()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
        var report = students.Select(s => new
        {
            studentId = s.StudentId,
            fullName = s.FullName,
            ela = new
            {
                status = s.ElaTier.Status,
                dataPoints = s.ElaTier.DataPoints,
                pendingReason = s.ElaTier.PendingReason,
                excluded = s.ElaTier.Evidence.Where(e => !e.Counted)
                    .Select(e => new { e.Source, e.Category, e.Date, e.ExclusionReason }),
            },
            math = new
            {
                status = s.MathTier.Status,
                dataPoints = s.MathTier.DataPoints,
                pendingReason = s.MathTier.PendingReason,
                excluded = s.MathTier.Evidence.Where(e => !e.Counted)
                    .Select(e => new { e.Source, e.Category, e.Date, e.ExclusionReason }),
            },
        }).ToList();

        var exclusionsBySourceAndReason = students
            .SelectMany(s => s.ElaTier.Evidence.Concat(s.MathTier.Evidence))
            .Where(e => !e.Counted)
            .GroupBy(e => new { e.Source, e.ExclusionReason })
            .Select(g => new { g.Key.Source, g.Key.ExclusionReason, count = g.Count() })
            .OrderByDescending(g => g.count);

        return Ok(new { summary = exclusionsBySourceAndReason, students = report });
    }

    // ── STN diagnostic (read-only): why does this student have no State Student Number? ──────────
    // For each active student with a blank/null Stn, re-parses every file currently sitting in the
    // landing-zone container (in memory, never stored) and classifies why no STN has landed on
    // them, without changing any matching/import logic:
    //   DUPLICATE_HOLDS_STN — another student record (any active state) has the same name and a
    //                         real STN — likely a soft-deleted or unmerged duplicate.
    //   SOURCE_NEAR_MISS    — a source row's name normalizes to the same name and carries an STN,
    //                         but today's exact/ordinal name comparison wouldn't match it.
    //   SOURCE_ROW_NO_STN   — a source row matches by name, but its STN cell is genuinely blank.
    //   NO_SOURCE_ROW       — no candidate row found at all (a coverage gap, not a code bug — e.g.
    //                         a K-2 student with no ILEARN row to backfill from).
    // No raw names are returned unless includeNames=true (super-admin only, audited) — every other
    // response uses studentId plus a non-reversible fingerprint so this is safe to run and share
    // without exposing any new PII beyond what the Students grid already shows.
    [HttpGet("stn-diagnostics")]
    public async Task<IActionResult> StnDiagnostics([FromQuery] bool includeNames = false, CancellationToken ct = default)
    {
        if (includeNames)
        {
            var isSuperAdmin = User.FindFirstValue("superAdmin") == "true";
            if (!isSuperAdmin) return Forbid();

            await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
                AuditEventType.Export, entityType: "DataQuality", entityId: "stn-diagnostics",
                details: "Ran STN diagnostics with includeNames=true (student names exposed in response).",
                ip: HttpContext.Connection.RemoteIpAddress?.ToString());
        }

        // activeOnly:false is deliberate — the STN-holding twin of a name mismatch may already be
        // soft-deleted by a prior dedup run.
        var (allStudents, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: false);
        var cohort = allStudents.Where(s => s.IsActive && string.IsNullOrWhiteSpace(s.Stn)).ToList();

        if (cohort.Count == 0)
            return Ok(new { totalMissingStn = 0, buckets = new Dictionary<string, int>(), students = Array.Empty<object>() });

        // Any other student (active or not) sharing a normalized name with a non-blank STN.
        var byNormalizedName = allStudents
            .Where(s => !string.IsNullOrWhiteSpace(s.FullName))
            .GroupBy(s => DiagNormalizeName(s.FullName))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Re-parse every landing-zone file in memory. Never written anywhere — purely for this
        // report. Best-effort: a single bad file shouldn't abort the whole diagnostic.
        var candidates = new List<(string Name, string? Stn, string UploadType, string FileName)>();
        List<LandingZoneFile> files;
        try { files = await blob.ListLandingZoneFilesAsync(ct); }
        catch (Exception ex)
        {
            return Ok(new
            {
                totalMissingStn = cohort.Count,
                error = $"Could not read landing-zone for source-row comparison: {ex.Message}",
                students = cohort.Select(s => DescribeStnGap(s, byNormalizedName, null, includeNames)).ToList(),
            });
        }

        foreach (var file in files)
        {
            try
            {
                var ext = Path.GetExtension(file.Name).ToLowerInvariant();
                if (ext is not ".csv" and not ".xlsx") { await file.Content.DisposeAsync(); continue; }

                using var buffer = new MemoryStream();
                await file.Content.CopyToAsync(buffer, ct);
                var formFile = new StreamFormFile(buffer.ToArray(), file.Name,
                    ext == ".csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                var uploadType = DetectUploadType(file.Name);
                var rows = ext == ".csv" ? await ParseCsvAsync(formFile, ct) : ParseXlsx(formFile);

                foreach (var row in rows)
                {
                    var name = ExtractRowNameForDiagnostics(row);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    // Broad header set — union of the demographics (narrow) and assessment (wide)
                    // lists, so we can tell "no STN column recognized here" apart from "STN cell
                    // genuinely blank". Anything picked up must still look like a real STN.
                    var stn = GetVal(row, "STN", "State_StudentNumber", "Student State ID",
                        "STUDENTS.State_StudentNumber", "State Student Number", "State ID", "SSID",
                        "Student State Number", "State Student ID Number", "ILEARN Student ID",
                        "IREAD Student ID", "Statewide Student ID", "State Student Identifier");
                    if (stn is not null && !LooksLikeStn(stn)) stn = null;
                    candidates.Add((name, stn, uploadType, file.Name));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "stn-diagnostics: failed to parse {File}, skipping", file.Name);
            }
            finally
            {
                await file.Content.DisposeAsync();
            }
        }

        var results = cohort.Select(s => DescribeStnGap(s, byNormalizedName, candidates, includeNames)).ToList();
        var buckets = results
            .GroupBy(r => r.Bucket)
            .ToDictionary(g => g.Key, g => g.Count());

        return Ok(new { totalMissingStn = cohort.Count, buckets, students = results });
    }

    private record StnDiagnosticEntry(
        string StudentId,
        string? FullName,
        string NameFingerprint,
        string? Grade,
        string ClassGroup,
        string Bucket,
        string? MismatchKind,
        string? MatchedFileName,
        string? MatchedUploadType,
        string? DuplicateStudentId);

    private StnDiagnosticEntry DescribeStnGap(
        StudentDocument student,
        Dictionary<string, List<StudentDocument>> byNormalizedName,
        List<(string Name, string? Stn, string UploadType, string FileName)>? candidates,
        bool includeNames)
    {
        var salt = configuration["Pii:FingerprintSalt"] ?? "lgs-stn-diagnostic-default-salt";
        var fingerprint = Fingerprint(student.FullName, salt);
        var normalizedTarget = DiagNormalizeName(student.FullName);

        string bucket;
        string? mismatchKind = null;
        string? matchedFileName = null;
        string? matchedUploadType = null;
        string? duplicateStudentId = null;

        var duplicate = byNormalizedName.TryGetValue(normalizedTarget, out var sameName)
            ? sameName.FirstOrDefault(other => other.StudentId != student.StudentId && !string.IsNullOrWhiteSpace(other.Stn))
            : null;

        if (duplicate is not null)
        {
            bucket = "DUPLICATE_HOLDS_STN";
            duplicateStudentId = duplicate.StudentId;
        }
        else if (candidates is null)
        {
            // Landing-zone couldn't be read at all — distinct from a genuine NO_SOURCE_ROW
            // coverage gap, which means "checked and found nothing."
            bucket = "SOURCE_UNAVAILABLE";
        }
        else
        {
            // Note: FirstOrDefault() on a non-nullable value tuple returns a default(all-null)
            // tuple rather than a true null when nothing matches, which would make an "is null"
            // check below always false. Project through a Nullable<> tuple first so an empty
            // result is a genuine null.
            // Exact-normalized match first (covers apostrophe/accent/whitespace-only differences).
            var exactMatch = candidates?
                .Where(c => DiagNormalizeName(c.Name) == normalizedTarget)
                .Select(c => ((string Name, string? Stn, string UploadType, string FileName)?)c)
                .FirstOrDefault();
            // Fall back to a token-subset match (e.g. a middle name present in one source but not
            // the other) only when no exact-normalized match exists.
            var subsetMatch = exactMatch is null
                ? candidates?
                    .Where(c => IsTokenSubsetMatch(c.Name, student.FullName))
                    .Select(c => ((string Name, string? Stn, string UploadType, string FileName)?)c)
                    .FirstOrDefault()
                : null;
            var match = exactMatch ?? subsetMatch;

            if (match is { } m)
            {
                matchedFileName = m.FileName;
                matchedUploadType = m.UploadType;
                if (!string.IsNullOrWhiteSpace(m.Stn))
                {
                    bucket = "SOURCE_NEAR_MISS";
                    mismatchKind = exactMatch is not null
                        ? ClassifyNameMismatch(m.Name, student.FullName)
                        : "TokenSubset";
                }
                else
                {
                    bucket = "SOURCE_ROW_NO_STN";
                }
            }
            else
            {
                bucket = "NO_SOURCE_ROW";
            }
        }

        return new StnDiagnosticEntry(
            StudentId: student.StudentId,
            FullName: includeNames ? student.FullName : null,
            NameFingerprint: fingerprint,
            Grade: student.Grade,
            ClassGroup: student.ClassGroup,
            Bucket: bucket,
            MismatchKind: mismatchKind,
            MatchedFileName: matchedFileName,
            MatchedUploadType: matchedUploadType,
            DuplicateStudentId: duplicateStudentId);
    }

    // ── Diagnostic-only name comparison helpers ──────────────────────────────────────────────────
    // Scoped to this read-only endpoint. Deliberately does NOT touch the production matching logic
    // in ProcessRowsAsync (:628-630) or CosmosDbService (:212-225, :299) — those are addressed by a
    // separate, more carefully tested shared normalizer once the diagnostic results are reviewed.

    // Reproduces the same name-assembly rules as ProcessRowsAsync (:444-466) — combined-name
    // columns first, then First+Last, then the "Last, First" flip — kept in sync manually since
    // extracting a shared helper is out of scope for a read-only diagnostic.
    internal static string? ExtractRowNameForDiagnostics(Dictionary<string, string> row)
    {
        var name = GetValExact(row, "FullName", "Full Name", "Name", "Student Name",
                               "Student Legal Name", "Legal Name");
        if (string.IsNullOrWhiteSpace(name))
        {
            var first = GetValExact(row, "First name", "First Name", "Student First Name", "FirstName",
                                    "Given Name", "Legal First Name", "Student Legal First Name");
            var last = GetValExact(row, "Last name", "Last Name", "Student Last Name", "LastName",
                                    "Family Name", "Surname", "Legal Last Name", "Student Legal Last Name");
            if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(last))
                name = $"{first} {last}";
        }
        if (!string.IsNullOrWhiteSpace(name) && name.Contains(','))
        {
            var parts = name.Split(',', 2);
            if (parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) && !string.IsNullOrWhiteSpace(parts[1]))
                name = $"{parts[1].Trim()} {parts[0].Trim()}";
        }
        return name;
    }

    // Folds quote-mark variants, dashes, accents, and whitespace so near-miss spellings compare
    // equal. Deliberately does not strip apostrophes/hyphens or drop tokens — see NameNormalization
    // design notes; a diagnostic false-negative (missed near-miss) is far preferable to silently
    // treating two different students' names as the same.
    internal static string DiagNormalizeName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var s = raw.Trim();
        s = s.Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'').Replace('´', '\'').Replace('`', '\'');
        s = s.Replace('–', '-').Replace('—', '-').Replace('‑', '-');
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var ch in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        s = sb.ToString().Normalize(NormalizationForm.FormC);
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s.ToLowerInvariant();
    }

    // True when the two names share the same first and last token but one has extra token(s)
    // (e.g. a middle name) the other lacks — "Keiry Marili Melendez Portillo" vs
    // "Keiry Melendez Portillo". Order-insensitive on the interior tokens only.
    internal static bool IsTokenSubsetMatch(string? a, string? b)
    {
        var tokensA = DiagNormalizeName(a).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tokensB = DiagNormalizeName(b).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokensA.Length < 2 || tokensB.Length < 2 || tokensA.Length == tokensB.Length) return false;
        if (tokensA[0] != tokensB[0]) return false; // first name must match
        if (tokensA[^1] != tokensB[^1]) return false; // last name must match
        var (shorter, longer) = tokensA.Length < tokensB.Length ? (tokensA, tokensB) : (tokensB, tokensA);
        return shorter.All(t => longer.Contains(t));
    }

    // Best-effort explanation of why today's raw comparison at ProcessRowsAsync:628-630
    // (`OrdinalIgnoreCase.Equals`) would miss two names that DiagNormalizeName treats as identical.
    internal static string ClassifyNameMismatch(string sourceName, string rosterName)
    {
        if (string.Equals(sourceName.Trim(), rosterName.Trim(), StringComparison.OrdinalIgnoreCase))
            return "AlreadyMatchable"; // normalized-equal AND ordinal-equal — likely a header/upload-type gap, not a name issue

        string QuoteFold(string s) => s.Replace('’', '\'').Replace('‘', '\'').Replace('ʼ', '\'').Replace('´', '\'').Replace('`', '\'');
        static string CollapseWs(string s) => Regex.Replace(s.Trim(), @"\s+", " ");

        var a = sourceName.Trim();
        var b = rosterName.Trim();
        if (string.Equals(QuoteFold(a), QuoteFold(b), StringComparison.OrdinalIgnoreCase)) return "ApostropheForm";
        if (string.Equals(CollapseWs(a), CollapseWs(b), StringComparison.OrdinalIgnoreCase)) return "Whitespace";

        var da = DiagNormalizeName(a);
        var db = DiagNormalizeName(b);
        var accentStrippedA = Regex.Replace(a.Normalize(NormalizationForm.FormD), @"\p{Mn}", "");
        var accentStrippedB = Regex.Replace(b.Normalize(NormalizationForm.FormD), @"\p{Mn}", "");
        if (string.Equals(accentStrippedA.Trim(), accentStrippedB.Trim(), StringComparison.OrdinalIgnoreCase)) return "Accent";

        return da == db ? "Combined" : "Other";
    }

    // Truncated, salted, non-reversible identifier for a name — lets the response correlate
    // students across a run without ever including or re-deriving the actual name.
    internal static string Fingerprint(string? name, string salt)
    {
        var normalized = DiagNormalizeName(name);
        var bytes = Encoding.UTF8.GetBytes(salt + "|" + normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    // ── Clean-cutover purge (super-admin only). Irreversible — deletes every student and
    // assessment document so LGS can re-upload source files under the corrected ingest logic
    // without needing a backfill of historical period/date metadata. ──
    [HttpPost("purge-all")]
    public async Task<IActionResult> PurgeAll([FromBody] PurgeAllRequestDto dto)
    {
        if (dto.Confirm != "DELETE ALL STUDENT AND ASSESSMENT DATA")
            return BadRequest(new { message = "Confirmation phrase did not match. This operation is irreversible and was not performed." });

        var assessmentCount = await cosmos.DeleteAllAssessmentsAsync();
        var studentCount = await cosmos.DeleteAllStudentsAsync();

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Delete, entityType: "PurgeAll", entityId: null,
            details: $"Purged all data for tier-engine clean cutover: {studentCount} students, {assessmentCount} assessments deleted.",
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(new { studentsDeleted = studentCount, assessmentsDeleted = assessmentCount });
    }

    public record PurgeAllRequestDto(string Confirm);

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

    // ── One-shot corrective re-import ────────────────────────────────────────────────
    // Replaces every stored record for one file, in the only order that is safe: the replacement
    // file is fetched, parsed and validated BEFORE anything is deleted, so a missing or unparseable
    // source aborts with the existing data untouched. Written because doing this by hand has two
    // traps. First, a plain re-upload cannot correct a changed date or period: the assessment
    // signature includes them, so corrected rows are stored alongside the wrong ones and then
    // compete in latest-wins dedup. Second, the historical duplicate-import bug left several upload
    // log rows sharing one filename — deleting one wipes every assessment for that name while the
    // remaining rows keep their content hashes, which then block the re-upload and leave the data
    // gone with no way back. This clears all of them together.
    [HttpPost("reimport-file")]
    public async Task<IActionResult> ReimportFile(
        [FromBody] ReimportFileRequestDto dto,
        [FromQuery] bool dryRun = true,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dto.FileName))
            return BadRequest(new { message = "fileName is required — the name recorded on the assessments to be replaced." });

        var storedName = dto.FileName.Trim();
        // The blob may be named differently from the stored records: the ADA-ZEL export is recorded
        // under a hand-renamed "..._Checkpoint3.csv" while the container holds "..._150626 PM.csv".
        var sourceName = string.IsNullOrWhiteSpace(dto.SourceFile) ? storedName : dto.SourceFile.Trim();

        var allLogs = await cosmos.GetUploadLogsAsync();
        var matchingLogs = allLogs
            .Where(l => !string.IsNullOrWhiteSpace(l.FileName) &&
                        (l.FileName.Trim().Equals(storedName, StringComparison.OrdinalIgnoreCase) ||
                         l.FileName.Trim().Equals(sourceName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // Delete by every exact spelling that exists, since the Cosmos query matches FileName
        // case-sensitively and records may predate a rename.
        var namesToPurge = matchingLogs.Select(l => l.FileName.Trim())
            .Append(storedName)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var allAssessments = await cosmos.GetAllAssessmentsAsync();
        var affectedAssessments = allAssessments
            .Where(a => namesToPurge.Any(n => string.Equals(a.FileName?.Trim(), n, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        // ── Fetch and validate the replacement before touching anything ──
        LandingZoneFile? match = null;
        List<LandingZoneFile> files;
        try { files = await blob.ListLandingZoneFilesAsync(ct); }
        catch (Exception ex)
        {
            return StatusCode(502, new { message = $"Could not read the source container: {ex.Message}. Nothing was deleted." });
        }

        byte[]? content = null;
        try
        {
            match = files.FirstOrDefault(f => f.Name.Trim().Equals(sourceName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                return NotFound(new
                {
                    message = $"\"{sourceName}\" was not found in the configured source container. Nothing was deleted.",
                    hint = "Check LandingZone:ContainerName points at the container holding the file, and that the name matches exactly.",
                    availableFiles = files.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList(),
                });
            }

            using var buffer = new MemoryStream();
            await match.Content.CopyToAsync(buffer, ct);
            content = buffer.ToArray();
        }
        finally
        {
            foreach (var f in files) await f.Content.DisposeAsync();
        }

        var ext = Path.GetExtension(sourceName).ToLowerInvariant();
        var formFile = new StreamFormFile(content, sourceName,
            ext == ".csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        List<Dictionary<string, string>> rows;
        try { rows = ext == ".csv" ? await ParseCsvAsync(formFile, ct) : ParseXlsx(formFile); }
        catch (Exception ex)
        {
            return BadRequest(new { message = $"\"{sourceName}\" could not be parsed: {ex.Message}. Nothing was deleted." });
        }

        if (rows.Count == 0)
            return BadRequest(new { message = $"\"{sourceName}\" contains no data rows. Nothing was deleted." });

        var headers = rows[0].Keys.ToList();
        var uploadType = ResolveUploadType(sourceName, headers, dto.UploadType);

        if (uploadType == "__SKIP__")
            return BadRequest(new { message = $"\"{sourceName}\" is a TEST_ file and is never imported. Nothing was deleted." });

        var schemaError = ValidateSchema(headers, uploadType);
        if (schemaError is not null)
            return BadRequest(new { message = $"Schema error for \"{uploadType}\": {schemaError}. Nothing was deleted." });

        var contentHash = ComputeSha256(formFile);
        var blockingLog = await cosmos.FindUploadLogByHashAsync(contentHash);
        var hashBlockedByOtherFile = blockingLog is not null && !matchingLogs.Any(l => l.Id == blockingLog.Id);

        if (dryRun)
        {
            return Ok(new
            {
                dryRun = true,
                plan = new
                {
                    storedName,
                    sourceName,
                    resolvedUploadType = uploadType,
                    uploadTypeFromFileName = DetectUploadType(sourceName),
                    uploadTypeFromColumns = DetectTypeFromColumns(headers),
                    sourceRows = rows.Count,
                    uploadLogRowsToDelete = matchingLogs.Select(l => new { l.Id, l.FileName, l.UploadType, l.UploadedAt, l.RecordCount }),
                    assessmentsToDelete = affectedAssessments.Count,
                    studentsAffected = affectedAssessments.Select(a => a.StudentId).Distinct().Count(),
                    fileNameSpellingsPurged = namesToPurge,
                    recalculateTiers = dto.RecalculateTiers,
                },
                warning = hashBlockedByOtherFile
                    ? $"This content is already logged under a different file (\"{blockingLog!.FileName}\"), whose log row will NOT be removed — the re-import would be rejected as a duplicate. Re-import that file instead, or delete its log row first."
                    : null,
                message = "Nothing was changed. Re-send with ?dryRun=false to apply.",
            });
        }

        if (hashBlockedByOtherFile)
            return Conflict(new
            {
                message = $"This content is already logged under \"{blockingLog!.FileName}\", which is not one of the log rows being replaced. " +
                          "The re-import would be rejected as a duplicate, so nothing was deleted.",
                conflictingLogId = blockingLog.Id,
            });

        // ── Apply: delete, then import ──
        foreach (var name in namesToPurge)
            await cosmos.DeleteAssessmentsByFileNameAsync(name);

        foreach (var log in matchingLogs)
            await cosmos.DeleteUploadLogAsync(log.Id, log.UploadedBy);

        var result = await ProcessRowsAsync(rows, uploadType, sourceName, ct);

        await cosmos.CreateUploadLogAsync(new UploadLogDocument
        {
            Id           = Guid.NewGuid().ToString(),
            UploadedBy   = CurrentAdminEmail,
            FileName     = sourceName,
            UploadType   = uploadType,
            UploadedAt   = DateTime.UtcNow.ToString("o"),
            RecordCount  = result.ImportedRows,
            SkippedCount = result.SkippedRows,
            ContentHash  = contentHash,
            Errors       = result.Errors,
        });

        int? tiersUpdated = null;
        if (dto.RecalculateTiers)
        {
            try
            {
                var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
                var eligible = students.Where(s => !s.AllSubjectsOverridden).ToList();
                tiersUpdated = await tierCalculation.ComputeAndApplyBatchAsync(eligible);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tier recalculation failed after re-importing {File}", sourceName);
            }
        }

        await audit.LogAsync(CurrentAdminId, CurrentAdminEmail,
            AuditEventType.Upload, entityType: "Reimport", entityId: sourceName,
            details: $"Corrective re-import of \"{sourceName}\" as {uploadType}: removed {affectedAssessments.Count} assessment(s) " +
                     $"and {matchingLogs.Count} upload log row(s) for [{string.Join(", ", namesToPurge)}], " +
                     $"imported {result.ImportedRows} row(s), skipped {result.SkippedRows}." +
                     (tiersUpdated is not null ? $" Recalculated {tiersUpdated} student tier(s)." : ""),
            ip: HttpContext.Connection.RemoteIpAddress?.ToString());

        return Ok(new
        {
            dryRun = false,
            storedName,
            sourceName,
            uploadType,
            deleted = new
            {
                assessments = affectedAssessments.Count,
                uploadLogRows = matchingLogs.Count,
                fileNameSpellings = namesToPurge,
            },
            imported = result,
            tiersUpdated,
        });
    }

    public record ReimportFileRequestDto(string FileName, string? SourceFile, string? UploadType, bool RecalculateTiers = true);

    /// <summary>Upload type for a re-import: an explicit override wins, then the filename, and when
    /// the filename yields nothing better than the "demographics" default the column signatures
    /// decide. Indiana's ILEARN exports are named e.g. "..._ADA-ZEL_StudentData_150626 PM.csv" with
    /// no ILEARN or Checkpoint token, so filename-only detection called them demographics and the
    /// file had to be renamed by hand before it could be imported at all.</summary>
    internal static string ResolveUploadType(string fileName, IReadOnlyList<string> headers, string? explicitType)
    {
        if (!string.IsNullOrWhiteSpace(explicitType)) return explicitType.Trim();

        var fromName = DetectUploadType(fileName);
        if (!fromName.Equals("demographics", StringComparison.OrdinalIgnoreCase)) return fromName;

        var fromColumns = DetectTypeFromColumns(headers);
        return fromColumns ?? fromName;
    }

    private async Task<ParseSummaryDto> ProcessRowsAsync(
        List<Dictionary<string, string>> rows, string uploadType, string fileName, CancellationToken ct)
    {
        int imported = 0, skipped = 0, duplicateAssessments = 0, correctedAssessments = 0;
        var errors = new List<string>();

        // Signatures of assessments already recorded for each student, loaded lazily on first
        // sight of that student and mapped to the existing document id so a corrected export
        // (same test event, different score/proficiency) can be upserted in place instead of
        // creating a second row. Guards against the same test event being stored twice — whether
        // from re-importing a file (the landing-zone path had no duplicate protection at all) or
        // from the client's overlapping exports, where one result legitimately appears in both a
        // subject-specific and a combined "Results" file.
        var seenAssessments = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

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
                // Handle "Last, First" (ILEARN StudentData: "Adams, Gionni") including a space
                // after the comma. The previous `!name.Contains(' ')` guard left those unmatched
                // against roster names stored as "First Last".
                if (!string.IsNullOrWhiteSpace(name) && name.Contains(','))
                {
                    var parts = name.Split(',', 2);
                    if (parts.Length == 2
                        && !string.IsNullOrWhiteSpace(parts[0])
                        && !string.IsNullOrWhiteSpace(parts[1]))
                        name = $"{parts[1].Trim()} {parts[0].Trim()}";
                }
                // ILEARN/IREAD/Acadience exports often have STN + DOB but no name column.
                // Demographics rows may still enrich via STN. Skip only when there is nothing
                // to match on.
                if (string.IsNullOrWhiteSpace(name) && uploadType != "demographics")
                {
                    var idHint = GetVal(row, "STN", "Student State ID", "State_StudentNumber",
                                         "Student_Number", "Student Number", "Local ID", "Local Student ID");
                    if (string.IsNullOrWhiteSpace(idHint)) { skipped++; continue; }
                }

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
                    {
                        student = await cosmos.FindStudentByStnAsync(stn);
                        // CP3 backfill stored some STNs with a leading T (ILEARN student ID);
                        // CP1/CP2 files use the numeric STN. Try both shapes.
                        if (student is null && stn.StartsWith("T", StringComparison.OrdinalIgnoreCase) && stn.Length > 1)
                            student = await cosmos.FindStudentByStnAsync(stn[1..]);
                        else if (student is null && !stn.StartsWith("T", StringComparison.OrdinalIgnoreCase))
                            student = await cosmos.FindStudentByStnAsync("T" + stn);
                    }
                    if (student is null && !string.IsNullOrWhiteSpace(localId))
                        student = await cosmos.FindStudentByLocalIdAsync(localId);
                    if (student is null && !string.IsNullOrWhiteSpace(name))
                    {
                        // Do not pass `name` as a search filter — ListStudentsAsync uses Contains,
                        // which can miss or over-filter. Load the roster and exact-match FullName.
                        var (allStudents, _) = await cosmos.ListStudentsAsync(1, 10_000, null, null);
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
                            EnrolDate  = DateTime.UtcNow.ToString("o"),
                            LastUpdated = DateTime.UtcNow.ToString("o")
                        };
                        await cosmos.UpsertStudentAsync(student);
                    }

                    if (student is null) { skipped++; continue; }

                    // Assessment rows often carry STN/DOB that the IXL auto-create stub never
                    // stored. Without those, a later PowerSchool file cannot match (LocalId → STN
                    // → name+DOB) and every student stays ClassGroup = "Unassigned".
                    var studentDirty = false;
                    if (string.IsNullOrWhiteSpace(student.Stn) && !string.IsNullOrWhiteSpace(stn))
                    { student.Stn = stn; studentDirty = true; }
                    if (string.IsNullOrWhiteSpace(student.LocalId) && !string.IsNullOrWhiteSpace(localId))
                    { student.LocalId = localId; studentDirty = true; }
                    if (string.IsNullOrWhiteSpace(student.Dob))
                    {
                        var rowDob = GetVal(row, "DOB", "Date of Birth", "Birth Date", "Student DOB", "STUDENTS.DOB");
                        if (!string.IsNullOrWhiteSpace(rowDob)) { student.Dob = rowDob; studentDirty = true; }
                    }

                    // Assessment uploads only ever wrote an AssessmentDocument, never the matched
                    // student record — so a student who only ever receives assessment files (no
                    // further demographics re-upload) kept whatever SourceFile it had at creation,
                    // which is blank for older records. Track the most recent file that touched
                    // this student in any way so "Source File" reflects reality. See QA issue #11.
                    if (!string.Equals(student.SourceFile, fileName, StringComparison.Ordinal))
                    {
                        student.SourceFile = fileName;
                        studentDirty = true;
                    }
                    if (studentDirty)
                    {
                        student.LastUpdated = DateTime.UtcNow.ToString("o");
                        await cosmos.UpsertStudentAsync(student);
                    }

                    var scoreRaw = GetVal(row, "Score", "Scale Score", "Overall Score",
                                         "Overall ELA score", "Overall math score", "Overall reading score",
                                         "Overall math scale score", "Overall ELA scale score",
                                         "Reading Composite Score", "Diagnostic level", "SmartScore");
                    double.TryParse(scoreRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var score);

                    var subject = GetVal(row, "Subject", "Content Area", "Test Subject")
                                  ?? AssessmentNormalization.DetectSubject(uploadType, fileName, row);
                    var rawProficiency = GetVal(row, "Proficiency Level", "Performance Level",
                                            "Status", "Achievement Level",
                                            "Overall math tier", "Overall ELA tier", "Overall reading tier",
                                            "Reading Composite Status");
                    // "Test Reason" and "Test Name" come first because that is where Indiana's
                    // ILEARN exports actually put the checkpoint ("ILEARN Checkpoint 3" /
                    // "ILEARN Mathematics Grade 6 Checkpoint 3, Opp 1: ..."). "Test OppNumber"
                    // holds "First Assessment", which identifies the attempt, not the checkpoint —
                    // when it was probed first, the period could only be recovered from the
                    // filename, so an export named ..._150626 PM.csv lost its period entirely and
                    // had to be renamed by hand before upload.
                    // "School Year" is deliberately absent: it is not a period. Acadience progress-
                    // monitoring exports carry "School Year" = "2025-2026" and no benchmark window,
                    // so probing it stored period="2025-2026" on every row — which the Acadience "*"
                    // wildcard weight would then happily accept as real evidence.
                    var periodRaw = GetVal(row, "Period", "Term", "Benchmark Period",
                                        "Test Reason", "Test Name",
                                        "Test OppNumber", "Assessment Window", "Test Window", "Checkpoint",
                                        "Diagnostic Window", "Snapshot");
                    var date = GetVal(row, "Date", "Date Taken", "Date of completion",
                                      "Test Date", "Reading Composite Date");

                    // Task 13: normalise I-Read pass/fail → standard proficiency labels
                    var proficiency = uploadType.Equals("IREAD", StringComparison.OrdinalIgnoreCase)
                        ? AssessmentNormalization.NormalizeIReadProficiency(rawProficiency)
                        : rawProficiency;

                    // Period normalization: CP1/CP2/CP3/SPRING for ILEARN, BOY/MOY/EOY for
                    // Acadience and IXL. Null (unresolved) is preserved as null rather than a raw
                    // fallback — the tier engine excludes evidence it cannot weight rather than
                    // silently defaulting a weight.
                    var period = AssessmentNormalization.NormalizePeriod(uploadType, periodRaw, fileName);

                    // The filename is a fallback, not a source of truth. Before "Test Reason" was
                    // probed, a CP3 export had to be renamed by hand to be recognised — and a
                    // mistyped rename would have applied the wrong evidence weight silently, with
                    // the file itself stating the correct checkpoint in a column nobody read. Warn
                    // when the two disagree; the column wins.
                    var periodFromColumn = AssessmentNormalization.NormalizePeriod(uploadType, periodRaw, "");
                    var periodFromFileName = AssessmentNormalization.NormalizePeriod(uploadType, null, fileName);
                    if (periodFromColumn is not null && periodFromFileName is not null &&
                        !string.Equals(periodFromColumn, periodFromFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        logger.LogWarning(
                            "Period mismatch in {File}: the file's own column says {FromColumn} but the " +
                            "filename says {FromFileName}. Using {Used} — check the filename is not mislabelled.",
                            fileName, periodFromColumn, periodFromFileName, periodFromColumn);
                    }

                    var normalizedSubject = AssessmentNormalization.NormalizeSubject(subject);
                    var finalScore = score > 0 ? (double?)score : null;
                    var dateIso = AssessmentNormalization.TryParseFlexibleDate(date, uploadType, out var dateAmbiguous);

                    // A test event is identified by student + type + subject + period + date.
                    // Period is part of the identity (not just date+score) so that a corrected score
                    // for the same checkpoint is recognised as the same event rather than counted
                    // twice, while two different checkpoints that happen to share a date/score are
                    // never collapsed into one (C-06). FileName is deliberately excluded: the same
                    // result arriving under two different filenames is still one event.
                    if (!seenAssessments.TryGetValue(student.StudentId, out var signatures))
                    {
                        var existing = await cosmos.GetAssessmentsAsync(student.StudentId);
                        // Grouped rather than ToDictionary: a student who already carries two rows
                        // sharing one signature — which the pre-dedup imports left behind in
                        // quantity — made ToDictionary throw "An item with the same key has already
                        // been added". That threw inside the per-row try, so the row was counted as
                        // an error and skipped, and every later row for that student failed too.
                        // Keep the first id; the extra rows are the duplicates being superseded.
                        signatures = existing
                            .GroupBy(a => AssessmentSignature(a.UploadType, a.Subject, a.Period, a.DateIso ?? a.Date),
                                     StringComparer.Ordinal)
                            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
                        seenAssessments[student.StudentId] = signatures;
                    }

                    var signature = AssessmentSignature(uploadType, normalizedSubject, period, dateIso ?? date);
                    if (signatures.TryGetValue(signature, out var existingAssessmentId))
                    {
                        // Same test event already recorded. If the value is unchanged, it's a true
                        // duplicate (e.g. the same file re-imported); skip it. If the score or
                        // proficiency differs, it's a corrected export (C-06) — update the existing
                        // record in place rather than creating a second one.
                        var existingAssessments = await cosmos.GetAssessmentsAsync(student.StudentId);
                        var existingAssessment = existingAssessments.FirstOrDefault(a => a.Id == existingAssessmentId);
                        var isCorrection = existingAssessment is not null &&
                            (existingAssessment.Score != finalScore || existingAssessment.Proficiency != proficiency);

                        if (!isCorrection)
                        {
                            duplicateAssessments++;
                            continue;
                        }

                        existingAssessment!.Score = finalScore;
                        existingAssessment.Proficiency = proficiency;
                        existingAssessment.PeriodRaw = periodRaw;
                        existingAssessment.Period = period;
                        existingAssessment.Date = date;
                        existingAssessment.DateIso = dateIso;
                        existingAssessment.DateAmbiguous = dateAmbiguous;
                        existingAssessment.FileName = fileName;
                        existingAssessment.UploadedAt = DateTime.UtcNow.ToString("o");
                        existingAssessment.RawFields = piiRedaction.RedactRawFields(row);
                        await cosmos.UpsertAssessmentAsync(existingAssessment);
                        correctedAssessments++;
                        continue;
                    }

                    await cosmos.CreateAssessmentAsync(new AssessmentDocument
                    {
                        Id         = Guid.NewGuid().ToString(),
                        StudentId  = student.StudentId,
                        UploadType = uploadType,
                        FileName   = fileName,
                        PeriodRaw  = periodRaw,
                        DateIso    = dateIso,
                        DateAmbiguous = dateAmbiguous,
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

        return new ParseSummaryDto(rows.Count, imported, skipped, errors, duplicateAssessments, correctedAssessments);
    }

    // Subject/period/proficiency normalization now lives in AssessmentNormalization (shared with
    // the tier engine and the data-quality/backfill tooling) — see calls above in ProcessRowsAsync.

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
                                  int DuplicateAssessments = 0, int CorrectedAssessments = 0);

    // Identifies a single test event: student (via the caller's per-student signature set) + type +
    // subject + period + date. Score is deliberately NOT part of the identity — a corrected score
    // for the same checkpoint must be recognised as the same event (C-06), not a second one.
    private static string AssessmentSignature(string? uploadType, string? subject, string? period, string? date)
        => string.Join('|',
            (uploadType ?? "").Trim().ToLowerInvariant(),
            (subject ?? "").Trim().ToLowerInvariant(),
            (period ?? "").Trim().ToUpperInvariant(),
            (date ?? "").Trim());

    // ─── Validation helpers ───────────────────────────────────────────────────

    private static string ComputeSha256(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // The upload types the row processor can actually store data for. Kept next to
    // RequiredColumnGroups so adding a type forces a decision about its schema at the same time.
    internal static readonly HashSet<string> SupportedUploadTypes =
        new(StringComparer.OrdinalIgnoreCase) { "demographics", "ILEARN", "IXL", "Acadience", "IREAD" };

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
    internal static string? DetectTypeFromColumns(IReadOnlyList<string> headers)
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
