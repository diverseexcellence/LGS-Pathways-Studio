using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/config")]
[Authorize]
public class ConfigController(ICosmosDbService cosmos) : ControllerBase
{
    private string CurrentAdminEmail => User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email) ?? "unknown";

    // BRD AI-1 / Task 8: GET current tier ruleset config (all admins can read)
    [HttpGet("tier-rules")]
    public async Task<IActionResult> GetTierRules()
    {
        var doc = await cosmos.GetTierRulesetConfigAsync();
        return Ok(doc);
    }

    // BRD AI-1 / Task 8: PUT update tier ruleset config (admin only)
    [HttpPut("tier-rules")]
    public async Task<IActionResult> PutTierRules([FromBody] TierRulesetUpdateDto dto)
    {
        var doc = await cosmos.GetTierRulesetConfigAsync();

        if (!string.IsNullOrWhiteSpace(dto.RulesetVersion)) doc.RulesetVersion   = dto.RulesetVersion;
        if (dto.PercentileCutoff.HasValue)                   doc.PercentileCutoff = dto.PercentileCutoff.Value;
        if (!string.IsNullOrWhiteSpace(dto.EffectiveDate))   doc.EffectiveDate    = dto.EffectiveDate;
        if (!string.IsNullOrWhiteSpace(dto.Description))     doc.Description      = dto.Description;

        doc.UpdatedAt = DateTime.UtcNow.ToString("o");
        doc.UpdatedBy = CurrentAdminEmail;

        await cosmos.UpsertTierRulesetConfigAsync(doc);
        return Ok(doc);
    }
}

public record TierRulesetUpdateDto(
    string? RulesetVersion,
    int? PercentileCutoff,
    string? EffectiveDate,
    string? Description);
