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

    // ─── KPIs (ELA growth + Math proficiency) ────────────────────────────────

    [HttpGet("kpis")]
    public async Task<IActionResult> Kpis()
    {
        var allAssessments = await cosmos.GetAllAssessmentsAsync();

        // ── Math proficiency % ────────────────────────────────────────────────
        // Group by studentId, pick latest Math assessment per student, resolve On/Above
        var mathByStudent = allAssessments
            .Where(a => NormalizeSubject(a.Subject) == "Math" && a.StudentId != null)
            .GroupBy(a => a.StudentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Date ?? a.UploadedAt).First());

        int mathTotal = mathByStudent.Count;
        int mathOnAbove = mathByStudent.Values.Count(a => ResolveOnAbove(a) == true);
        double? mathPct = mathTotal > 0 ? Math.Round((double)mathOnAbove / mathTotal * 100, 1) : null;

        // ── ELA growth % ─────────────────────────────────────────────────────
        // Per student: earliest vs latest ELA score. Average the deltas across students who have ≥2 records.
        var elaByStudent = allAssessments
            .Where(a => NormalizeSubject(a.Subject) == "ELA" && a.StudentId != null && a.Score.HasValue)
            .GroupBy(a => a.StudentId)
            .Where(g => g.Count() >= 2)
            .ToList();

        double? elaGrowth = null;
        if (elaByStudent.Count > 0)
        {
            var deltas = elaByStudent.Select(g =>
            {
                var ordered = g.OrderBy(a => a.Date ?? a.UploadedAt).ToList();
                return ordered.Last().Score!.Value - ordered.First().Score!.Value;
            }).ToList();
            elaGrowth = Math.Round(deltas.Average(), 1);
        }

        return Ok(new
        {
            mathProficiencyPct = mathPct,
            mathStudentsTotal = mathTotal,
            mathStudentsOnAbove = mathOnAbove,
            elaGrowthAvgDelta = elaGrowth,
            elaStudentsWithGrowthData = elaByStudent.Count,
        });
    }

    // ─── Academic growth timeline ─────────────────────────────────────────────

    [HttpGet("timeline")]
    public async Task<IActionResult> Timeline()
    {
        var allAssessments = await cosmos.GetAllAssessmentsAsync();

        // Group by calendar month (yyyy-MM), average score per subject per month
        var monthMap = new Dictionary<string, (List<double> ela, List<double> math)>();

        foreach (var a in allAssessments)
        {
            if (!a.Score.HasValue) continue;
            var subject = NormalizeSubject(a.Subject);
            if (subject != "ELA" && subject != "Math") continue;

            var dateStr = a.Date ?? a.UploadedAt;
            if (!DateTime.TryParse(dateStr, out var dt)) continue;
            var key = dt.ToString("yyyy-MM");

            if (!monthMap.TryGetValue(key, out var bucket))
            {
                bucket = (new List<double>(), new List<double>());
                monthMap[key] = bucket;
            }

            if (subject == "ELA") bucket.ela.Add(a.Score.Value);
            else bucket.math.Add(a.Score.Value);
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

    [HttpGet("by-grade")]
    public async Task<IActionResult> ByGrade()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var gradeMap = new Dictionary<string, GradeStats>();

        foreach (var s in students)
        {
            // BRD DB-4: only Finalized students in tier distribution
            if (s.TierStatus != "Finalized") continue;

            var grade = NormalizeGrade(s.Grade);
            if (!gradeMap.TryGetValue(grade, out var gs))
            {
                gs = new GradeStats { Grade = grade };
                gradeMap[grade] = gs;
            }

            if (s.Tier == "Tier 1") gs.Tier1++;
            else if (s.Tier == "Tier 2") gs.Tier2++;
            else if (s.Tier == "Tier 3") gs.Tier3++;
        }

        var result = gradeMap.Values
            .OrderBy(g => GradeSortKey(g.Grade))
            .Select(g => new
            {
                grade = g.Grade,
                tier1 = g.Tier1,
                tier2 = g.Tier2,
                tier3 = g.Tier3,
                total = g.Tier1 + g.Tier2 + g.Tier3,
            });

        return Ok(result);
    }

    [HttpGet("by-grade/{grade}/teachers")]
    public async Task<IActionResult> TeachersByGrade(string grade)
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var teacherMap = new Dictionary<string, TeacherStats>();

        foreach (var s in students)
        {
            if (NormalizeGrade(s.Grade) != grade) continue;
            // BRD DB-4: only Finalized students in tier distribution
            if (s.TierStatus != "Finalized") continue;

            var teacher = s.HomeRoom ?? s.ClassGroup ?? "Unassigned";
            if (!teacherMap.TryGetValue(teacher, out var ts))
            {
                ts = new TeacherStats { Teacher = teacher };
                teacherMap[teacher] = ts;
            }

            ts.Total++;
            if (s.Tier == "Tier 1") ts.Tier1++;
            else if (s.Tier == "Tier 2") ts.Tier2++;
            else if (s.Tier == "Tier 3") ts.Tier3++;
        }

        var result = teacherMap.Values
            .OrderByDescending(t => t.Total)
            .Select(t => new
            {
                teacher = t.Teacher,
                tier1 = t.Tier1,
                tier2 = t.Tier2,
                tier3 = t.Tier3,
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
                tier = s.Tier,
                tierStatus = s.TierStatus,
                classGroup = s.ClassGroup,
                homeRoom = s.HomeRoom,
            });

        return Ok(result);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeSubject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var s = raw.Trim();
        if (s.Equals("Reading", StringComparison.OrdinalIgnoreCase)) return "Reading";
        if (s.Contains("ELA", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("English", StringComparison.OrdinalIgnoreCase) ||
            s.Contains("Language", StringComparison.OrdinalIgnoreCase)) return "ELA";
        if (s.Contains("Math", StringComparison.OrdinalIgnoreCase)) return "Math";
        return s;
    }

    private static bool? ResolveOnAbove(AssessmentDocument a)
    {
        var p = (a.Proficiency ?? "").ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(p))
        {
            if (p.Contains("far below") || p.Contains("below") || p.Contains("approaching") ||
                p.Contains("tier 2") || p.Contains("tier 3")) return false;
            if (p.Contains("above") || p.Contains("on grade") || p.Contains("at grade") ||
                p.Contains("at proficiency") || p.Contains("proficient") || p.Contains("meets") ||
                p.Contains("exceeds") || p.Contains("mid above") || p.Contains("early on") ||
                p.Contains("tier 1")) return true;
        }
        // Fallback: scan RawFields for percentile
        foreach (var kv in a.RawFields)
        {
            if (kv.Key.Contains("percentile", StringComparison.OrdinalIgnoreCase) &&
                double.TryParse(kv.Value, out var pct))
                return pct >= 40;
        }
        return null;
    }

    private static string NormalizeGrade(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Unknown";
        var cleaned = raw.Trim().ToUpperInvariant()
            .Replace("GRADE", "").Replace("GR.", "").Replace("GR", "").Trim();
        if (cleaned is "K" or "KG" or "KINDERGARTEN" or "0") return "K";
        if (int.TryParse(cleaned, out var n)) return n.ToString();
        return raw.Trim();
    }

    private static int GradeSortKey(string grade) => grade switch
    {
        "K" => 0,
        _ => int.TryParse(grade, out var n) ? n : 99,
    };

    private class GradeStats { public string Grade { get; set; } = ""; public int Tier1, Tier2, Tier3; }
    private class TeacherStats { public string Teacher { get; set; } = ""; public int Tier1, Tier2, Tier3, Total; }
}

public record SetTargetGoalRequest(int GoalPct);
