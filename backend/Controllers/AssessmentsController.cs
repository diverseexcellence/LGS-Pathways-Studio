using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/assessments")]
[Authorize]
public class AssessmentsController(ICosmosDbService cosmos) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string studentId, [FromQuery] string? subject = null)
    {
        if (string.IsNullOrWhiteSpace(studentId)) return BadRequest(new { message = "studentId required" });
        var items = await cosmos.GetAssessmentsAsync(studentId, subject);
        return Ok(items);
    }

    [HttpDelete("all")]
    public async Task<IActionResult> DeleteAll()
    {
        var deleted = await cosmos.DeleteAllAssessmentsAsync();
        return Ok(new { deleted });
    }
}
