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

    // BRD AI-1 / Task 8: PUT update tier ruleset config (admin only). Patch semantics — only
    // supplied fields change. Every dictionary/list field REPLACES the existing one wholesale
    // (there is no per-key merge), matching the ObjectCreationHandling.Replace behaviour used to
    // deserialize the document, so an admin removing a period from evidenceWeights.ILEARN by
    // omitting it actually removes it.
    [HttpPut("tier-rules")]
    public async Task<IActionResult> PutTierRules([FromBody] TierRulesetUpdateDto dto)
    {
        var doc = await cosmos.GetTierRulesetConfigAsync();
        var validationError = Validate(dto);
        if (validationError is not null) return BadRequest(new { message = validationError });

        // A substantive rule change must bump the version — that is what keeps the audit
        // reasoning string ("Ruleset v2.1") meaningful as a point-in-time reference.
        var substantiveChange = dto.CategoryValues is not null || dto.SharedCategoryValues is not null ||
            dto.EvidenceWeights is not null || dto.ExcludedSources is not null ||
            dto.SourceSubjectOverrides is not null || dto.TierThresholds is not null ||
            dto.MinDataPoints.HasValue || dto.UnknownPeriodWeight.HasValue;
        if (substantiveChange && (string.IsNullOrWhiteSpace(dto.RulesetVersion) || dto.RulesetVersion == doc.RulesetVersion))
            return BadRequest(new { message = "A substantive ruleset change requires a new rulesetVersion." });

        if (!string.IsNullOrWhiteSpace(dto.RulesetVersion)) doc.RulesetVersion = dto.RulesetVersion;
        if (dto.PercentileCutoff.HasValue) doc.PercentileCutoff = dto.PercentileCutoff.Value;
        if (!string.IsNullOrWhiteSpace(dto.EffectiveDate)) doc.EffectiveDate = dto.EffectiveDate;
        if (!string.IsNullOrWhiteSpace(dto.Description)) doc.Description = dto.Description;
        if (dto.CategoryValues is not null) doc.CategoryValues = dto.CategoryValues;
        if (dto.SharedCategoryValues is not null) doc.SharedCategoryValues = dto.SharedCategoryValues;
        if (dto.EvidenceWeights is not null) doc.EvidenceWeights = dto.EvidenceWeights;
        if (dto.ExcludedSources is not null) doc.ExcludedSources = dto.ExcludedSources;
        if (dto.SourceSubjectOverrides is not null) doc.SourceSubjectOverrides = dto.SourceSubjectOverrides;
        if (dto.TierThresholds is not null) doc.TierThresholds = dto.TierThresholds;
        if (dto.MinDataPoints.HasValue) doc.MinDataPoints = dto.MinDataPoints.Value;
        if (dto.ScoreDecimals.HasValue) doc.ScoreDecimals = dto.ScoreDecimals.Value;
        if (dto.UnknownPeriodWeight.HasValue) doc.UnknownPeriodWeight = dto.UnknownPeriodWeight;
        if (dto.PercentileFallbackEnabled.HasValue) doc.PercentileFallbackEnabled = dto.PercentileFallbackEnabled.Value;
        if (dto.IxlPeriodFromDateFallback.HasValue) doc.IxlPeriodFromDateFallback = dto.IxlPeriodFromDateFallback.Value;

        doc.UpdatedAt = DateTime.UtcNow.ToString("o");
        doc.UpdatedBy = CurrentAdminEmail;

        await cosmos.UpsertTierRulesetConfigAsync(doc);
        return Ok(doc);
    }

    private static string? Validate(TierRulesetUpdateDto dto)
    {
        if (dto.CategoryValues is not null)
            foreach (var (source, map) in dto.CategoryValues)
                foreach (var (label, value) in map)
                    if (value is < 0 or > 3) return $"categoryValues.{source}.{label} must be 0-3.";

        if (dto.SharedCategoryValues is not null)
            foreach (var (label, value) in dto.SharedCategoryValues)
                if (value is < 0 or > 3) return $"sharedCategoryValues.{label} must be 0-3.";

        if (dto.EvidenceWeights is not null)
            foreach (var (source, periods) in dto.EvidenceWeights)
                foreach (var (period, weight) in periods)
                    if (weight <= 0) return $"evidenceWeights.{source}.{period} must be greater than 0.";

        if (dto.TierThresholds is not null)
        {
            if (dto.TierThresholds.Count == 0) return "tierThresholds must not be empty.";
            if (!dto.TierThresholds.Any(t => t.MinScoreInclusive <= 0)) return "tierThresholds must cover a score of 0.00 (include a threshold with minScoreInclusive <= 0).";
            if (!dto.TierThresholds.Any(t => t.MinScoreInclusive >= 2))
                return "tierThresholds should include a Tier 1 boundary at or below 2.00 to keep the 0-3 range covered.";
        }

        if (dto.MinDataPoints is < 1) return "minDataPoints must be at least 1.";
        if (dto.ScoreDecimals is < 0 or > 4) return "scoreDecimals must be between 0 and 4.";

        return null;
    }
}

public record TierRulesetUpdateDto(
    string? RulesetVersion,
    int? PercentileCutoff,
    string? EffectiveDate,
    string? Description,
    Dictionary<string, Dictionary<string, int>>? CategoryValues,
    Dictionary<string, int>? SharedCategoryValues,
    Dictionary<string, Dictionary<string, double>>? EvidenceWeights,
    List<string>? ExcludedSources,
    Dictionary<string, string>? SourceSubjectOverrides,
    List<TierThreshold>? TierThresholds,
    int? MinDataPoints,
    int? ScoreDecimals,
    double? UnknownPeriodWeight,
    bool? PercentileFallbackEnabled,
    bool? IxlPeriodFromDateFallback);
