using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/audit")]
[Authorize]
public class AuditController(ICosmosDbService cosmos) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100,
        [FromQuery] string? eventType = null)
    {
        var isSuperAdmin = User.FindFirstValue("superAdmin") == "true";
        if (!isSuperAdmin) return Forbid();

        var (items, total) = await cosmos.GetAuditLogsAsync(page, pageSize, eventType);
        return Ok(new { items, total, page, pageSize });
    }
}
