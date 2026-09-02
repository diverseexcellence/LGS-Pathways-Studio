using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/dashboard")]
[Authorize]
public class DashboardController(ICosmosDbService cosmos) : ControllerBase
{
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";

    // ─── Target Goal ──────────────────────────────────────────────────────────

    [HttpGet("target-goal")]
    public async Task<IActionResult> GetTargetGoal()
    {
        var doc = await cosmos.GetTargetGoalAsync();
        return Ok(new { goalPct = doc.GoalPct, updatedAt = doc.UpdatedAt, updatedBy = doc.UpdatedBy });
    }

    [HttpPut("target-goal")]
    public async Task<IActionResult> SetTargetGoal([FromBody] SetTargetGoalRequest req)
    {
        if (req.GoalPct < 1 || req.GoalPct > 100)
            return BadRequest(new { message = "goalPct must be between 1 and 100." });

        var existing = await cosmos.GetTargetGoalAsync();
        existing.GoalPct = req.GoalPct;
        existing.UpdatedAt = DateTime.UtcNow.ToString("o");
        existing.UpdatedBy = CurrentAdminEmail;
        await cosmos.UpsertTargetGoalAsync(existing);
        return Ok(new { goalPct = existing.GoalPct });
    }

    // ─── KPIs (ELA growth + Math proficiency + tier counts) ──────────────────

    [HttpGet("kpis")]
    public async Task<IActionResult> Kpis()
    {
        var allAssessments = await cosmos.GetAllAssessmentsAsync();
        var ruleset = await cosmos.GetTierRulesetConfigAsync();

        // ── Math proficiency % ────────────────────────────────────────────────
        // Group by studentId, pick latest Math assessment per student, resolve its 0-3 value via
        // the same normalizer the tier engine uses (no percentile fallback — TR-003).
        var mathByStudent = allAssessments
            .Where(a => AssessmentNormalization.NormalizeSubject(a.Subject) == "Math" && a.StudentId != null)
            .GroupBy(a => a.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.DateIso ?? a.Date ?? a.UploadedAt).First());

        int mathTotal = mathByStudent.Count;
        int mathOnAbove = mathByStudent.Values.Count(a =>
            PerformanceLevelNormalizer.TryResolve(a.UploadType, a.Proficiency, ruleset, out var v) && v >= 2);
        double? mathPct = mathTotal > 0 ? Math.Round((double)mathOnAbove / mathTotal * 100, 1) : null;

        // ── Tier distribution (System Recommended + Finalized subjects — see dashboard gating) ──
        var (allStudents, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);
        var elaTiered = allStudents.Where(s => s.ElaTier.Status != "Pending").ToList();
        var mathTiered = allStudents.Where(s => s.MathTier.Status != "Pending").ToList();
        var elaTierCounts = new
        {
            tier1 = elaTiered.Count(s => s.ElaTier.Tier == "Tier 1"),
            tier2 = elaTiered.Count(s => s.ElaTier.Tier == "Tier 2"),
            tier3 = elaTiered.Count(s => s.ElaTier.Tier == "Tier 3"),
            pending = allStudents.Count - elaTiered.Count,
        };
        var mathTierCounts = new
        {
            tier1 = mathTiered.Count(s => s.MathTier.Tier == "Tier 1"),
            tier2 = mathTiered.Count(s => s.MathTier.Tier == "Tier 2"),
            tier3 = mathTiered.Count(s => s.MathTier.Tier == "Tier 3"),
            pending = allStudents.Count - mathTiered.Count,
        };

        // ── ELA growth (same instrument only) ────────────────────────────────
        // IXL diagnostic scores and ILEARN scale scores are different units; subtracting them
        // is not growth. Require ≥2 scored ELA results from one upload type per student.
        var (elaGrowth, elaGrowthStudents) = DashboardMetrics.ElaSameInstrumentGrowth(allAssessments);

        return Ok(new
        {
            mathProficiencyPct = mathPct,
            mathStudentsTotal = mathTotal,
            mathStudentsOnAbove = mathOnAbove,
            elaGrowthAvgDelta = elaGrowth,
            elaStudentsWithGrowthData = elaGrowthStudents,
            elaTierCounts,
            mathTierCounts,
        });
    }

    // ─── Academic growth timeline ─────────────────────────────────────────────

    [HttpGet("timeline")]
    public async Task<IActionResult> Timeline()
    {
        var allAssessments = await cosmos.GetAllAssessmentsAsync();
        var ruleset = await cosmos.GetTierRulesetConfigAsync();

        // Monthly average of the shared 0–3 proficiency scale (same resolver as the tier engine).
        // Raw scores cannot be averaged across IXL and ILEARN.
        var monthMap = new Dictionary<string, (List<double> ela, List<double> math)>();

        foreach (var a in allAssessments)
        {
            var subject = AssessmentNormalization.NormalizeSubject(a.Subject);
            if (subject != "ELA" && subject != "Math") continue;
            if (!PerformanceLevelNormalizer.TryResolve(a.UploadType, a.Proficiency, ruleset, out var value))
                continue;

            var dateStr = a.DateIso ?? a.Date ?? a.UploadedAt;
            if (!DateTime.TryParse(dateStr, out var dt)) continue;
            var key = dt.ToString("yyyy-MM");

            if (!monthMap.TryGetValue(key, out var bucket))
            {
                bucket = (new List<double>(), new List<double>());
                monthMap[key] = bucket;
            }

            if (subject == "ELA") bucket.ela.Add(value);
            else bucket.math.Add(value);
        }

        var result = monthMap
            .OrderBy(kv => kv.Key)
            .Select(kv =>
            {
                var dt = DateTime.ParseExact(kv.Key, "yyyy-MM", null);
                return new
                {
                    month = dt.ToString("MMM"),
                    year = dt.Year,
                    monthKey = kv.Key,
                    ela = kv.Value.ela.Count > 0 ? (double?)Math.Round(kv.Value.ela.Average(), 1) : null,
                    math = kv.Value.math.Count > 0 ? (double?)Math.Round(kv.Value.math.Average(), 1) : null,
                };
            })
            .ToList();

        return Ok(result);
    }

    // ─── Grade drill-down ─────────────────────────────────────────────────────

    // Selects the ELA or Math SubjectTier for a student. "?subject=ela|math" — defaults to ela.
    private static SubjectTier SubjectOf(StudentDocument s, string? subject) =>
        string.Equals(subject, "math", StringComparison.OrdinalIgnoreCase) ? s.MathTier : s.ElaTier;

    [HttpGet("by-grade")]
    public async Task<IActionResult> ByGrade([FromQuery] string? subject = "ela")
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var gradeMap = new Dictionary<string, GradeStats>();

        foreach (var s in students)
        {
            var t = SubjectOf(s, subject);
            var grade = NormalizeGrade(s.Grade);
            if (!gradeMap.TryGetValue(grade, out var gs))
            {
                gs = new GradeStats { Grade = grade };
                gradeMap[grade] = gs;
            }

            // Per-subject tier is included once the engine has produced a recommendation —
            // System Recommended or Finalized — not gated to Finalized-only (that would show 0
            // for every subject until every student is individually finalized). Students still
            // awaiting a recommendation are tracked as Pending so the grade total covers everyone.
            if (t.Status == "Pending") { gs.Pending++; continue; }

            if (t.Tier == "Tier 1") gs.Tier1++;
            else if (t.Tier == "Tier 2") gs.Tier2++;
            else if (t.Tier == "Tier 3") gs.Tier3++;
        }

        var result = gradeMap.Values
            .OrderBy(g => GradeSortKey(g.Grade))
            .Select(g => new
            {
                grade = g.Grade,
                tier1 = g.Tier1,
                tier2 = g.Tier2,
                tier3 = g.Tier3,
                pending = g.Pending,
                total = g.Tier1 + g.Tier2 + g.Tier3 + g.Pending,
            });

        return Ok(result);
    }

    [HttpGet("by-grade/{grade}/teachers")]
    public async Task<IActionResult> TeachersByGrade(string grade, [FromQuery] string? subject = "ela")
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var teacherMap = new Dictionary<string, TeacherStats>();

        foreach (var s in students)
        {
            if (NormalizeGrade(s.Grade) != grade) continue;
            var t = SubjectOf(s, subject);

            var teacher = s.HomeRoom ?? s.ClassGroup ?? "Unassigned";
            if (!teacherMap.TryGetValue(teacher, out var ts))
            {
                ts = new TeacherStats { Teacher = teacher };
                teacherMap[teacher] = ts;
            }

            ts.Total++;
            if (t.Status == "Pending") { ts.Pending++; continue; }

            if (t.Tier == "Tier 1") ts.Tier1++;
            else if (t.Tier == "Tier 2") ts.Tier2++;
            else if (t.Tier == "Tier 3") ts.Tier3++;
        }

        var result = teacherMap.Values
            .OrderByDescending(t => t.Total)
            .Select(t => new
            {
                teacher = t.Teacher,
                tier1 = t.Tier1,
                tier2 = t.Tier2,
                tier3 = t.Tier3,
                pending = t.Pending,
                total = t.Total,
            });

        return Ok(result);
    }

    [HttpGet("by-grade/{grade}/students")]
    public async Task<IActionResult> StudentsByGrade(string grade)
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var result = students
            .Where(s => NormalizeGrade(s.Grade) == grade)
            .OrderBy(s => s.FullName)
            .Select(s => new
            {
                studentId = s.StudentId,
                fullName = s.FullName,
                elaTier = s.ElaTier.Tier,
                elaTierStatus = s.ElaTier.Status,
                mathTier = s.MathTier.Tier,
                mathTierStatus = s.MathTier.Status,
                classGroup = s.ClassGroup,
                homeRoom = s.HomeRoom,
            });

        return Ok(result);
    }

    // ─── Geographic Distribution (BRD DB-12) ─────────────────────────────────
    // Returns tier counts grouped by ZIP code, for both subjects in one payload so the
    // frontend's ELA/Math toggle is instant. No simulated socio-economic data.
    // Coordinates are resolved client-side via OpenStreetMap Nominatim.

    [HttpGet("geographic")]
    public async Task<IActionResult> Geographic()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 50_000, null, null, activeOnly: true);

        var grouped = students
            .Where(s => !string.IsNullOrWhiteSpace(s.ZipCode))
            .GroupBy(s => s.ZipCode!.Trim())
            .Select(g => new
            {
                zip    = g.Key,
                total  = g.Count(),
                elaTier1  = g.Count(s => s.ElaTier.Status != "Pending" && s.ElaTier.Tier == "Tier 1"),
                elaTier2  = g.Count(s => s.ElaTier.Status != "Pending" && s.ElaTier.Tier == "Tier 2"),
                elaTier3  = g.Count(s => s.ElaTier.Status != "Pending" && s.ElaTier.Tier == "Tier 3"),
                mathTier1 = g.Count(s => s.MathTier.Status != "Pending" && s.MathTier.Tier == "Tier 1"),
                mathTier2 = g.Count(s => s.MathTier.Status != "Pending" && s.MathTier.Tier == "Tier 2"),
                mathTier3 = g.Count(s => s.MathTier.Status != "Pending" && s.MathTier.Tier == "Tier 3"),
            })
            .OrderByDescending(r => r.total)
            .ToList();

        return Ok(grouped);
    }

    // ─── Grade-Level Proficiency (BRD DB-5) ──────────────────────────────────
    // Segments by actual assessment proficiency bands (Above/On/Approaching/Below),
    // NOT tier assignment.  Uses the most recent assessment per student per subject.

    [HttpGet("by-grade-proficiency")]
    public async Task<IActionResult> ByGradeProficiency()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);
        var allAssessments = await cosmos.GetAllAssessmentsAsync();
        var ruleset = await cosmos.GetTierRulesetConfigAsync();

        // Index: studentId → most recent ELA assessment, most recent Math assessment
        var latestByStudentSubject = allAssessments
            .Where(a => AssessmentNormalization.NormalizeSubject(a.Subject) is "ELA" or "Math")
            .GroupBy(a => $"{a.StudentId}|{AssessmentNormalization.NormalizeSubject(a.Subject)}")
            .Select(g => g.OrderByDescending(a => a.DateIso ?? a.Date ?? a.UploadedAt).First())
            .ToList();

        // grade → { above, on, approaching, below, noData }
        var gradeMap = new Dictionary<string, ProficiencyBands>();

        foreach (var s in students)
        {
            var grade = NormalizeGrade(s.Grade);
            if (!gradeMap.TryGetValue(grade, out var bands))
            {
                bands = new ProficiencyBands();
                gradeMap[grade] = bands;
            }

            var studentAssessments = latestByStudentSubject
                .Where(a => a.StudentId == s.StudentId)
                .ToList();

            if (studentAssessments.Count == 0) { bands.NoData++; continue; }

            // Resolve the lowest proficiency band across ELA + Math (conservative signal)
            var proficiencies = studentAssessments
                .Select(a => PerformanceLevelNormalizer.TryResolveBand(a.UploadType, a.Proficiency, ruleset))
                .Where(p => p != null)
                .ToList();

            if (proficiencies.Count == 0) { bands.NoData++; continue; }

            // Use lowest band: Below > Approaching > On > Above
            var worst = proficiencies.OrderBy(BandSortKey).First()!;
            switch (worst)
            {
                case "above":       bands.Above++;      break;
                case "on":          bands.On++;         break;
                case "approaching": bands.Approaching++;break;
                default:            bands.Below++;      break;
            }
        }

        var result = gradeMap
            .OrderBy(kv => GradeSortKey(kv.Key))
            .Select(kv =>
            {
                var b = kv.Value;
                var total = b.Above + b.On + b.Approaching + b.Below + b.NoData;
                if (total == 0) return null;
                var scoredTotal = b.Above + b.On + b.Approaching + b.Below;
                var t = scoredTotal > 0 ? scoredTotal : 1;
                var above      = Math.Round((double)b.Above / t * 100);
                var on         = Math.Round((double)b.On / t * 100);
                var approaching = Math.Round((double)b.Approaching / t * 100);
                return new
                {
                    grade       = kv.Key,
                    above       = (int)above,
                    on          = (int)on,
                    approaching = (int)approaching,
                    below       = 100 - (int)above - (int)on - (int)approaching,
                    totalStudents = total,
                };
            })
            .Where(r => r != null);

        return Ok(result);
    }

    // Proficiency-band resolution now goes through PerformanceLevelNormalizer.TryResolveBand
    // (shared with the tier engine) — see ByGradeProficiency above. No percentile fallback (TR-003).

    private static int BandSortKey(string? b) => b switch
    {
        "below" => 0, "approaching" => 1, "on" => 2, "above" => 3, _ => 4
    };

    private class ProficiencyBands
    {
        public int Above, On, Approaching, Below, NoData;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────
    // Subject and performance-level resolution now go through AssessmentNormalization /
    // PerformanceLevelNormalizer (shared with the tier engine) — this used to be a second,
    // divergent copy of both that could disagree with the stored tier for the same student.

    private static string NormalizeGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var cleaned = raw.Trim().ToUpperInvariant()
            .Replace("GRADE", "").Replace("GR.", "").Replace("GR", "").Trim();
        // "-1" = Kindergarten — confirmed by LGS (Velvet Wright) on the 2026-08-14 client demo call.
        if (cleaned is "K" or "KG" or "KINDERGARTEN" or "0" or "-1") return "K";
        if (int.TryParse(cleaned, out var n)) return n.ToString();
        return raw.Trim();
    }

    private static int GradeSortKey(string grade) => grade switch
    {
        "K" => 0,
        _ => int.TryParse(grade, out var n) ? n : 99,
    };

    private class GradeStats { public string Grade { get; set; } = ""; public int Tier1, Tier2, Tier3, Pending; }
    private class TeacherStats { public string Teacher { get; set; } = ""; public int Tier1, Tier2, Tier3, Pending, Total; }
}

public record SetTargetGoalRequest(int GoalPct);
