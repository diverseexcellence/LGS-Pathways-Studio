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

    // ─── Grade drill-down ─────────────────────────────────────────────────────

    [HttpGet("by-grade")]
    public async Task<IActionResult> ByGrade()
    {
        var (students, _) = await cosmos.ListStudentsAsync(1, 10000, null, null, activeOnly: true);

        var gradeMap = new Dictionary<string, GradeStats>();

        foreach (var s in students)
        {
            if (s.TierStatus == "Pending") continue;

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
            if (s.TierStatus == "Pending") continue;

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
