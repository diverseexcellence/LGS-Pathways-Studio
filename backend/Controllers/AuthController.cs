using LgsImpact.Api.Models;
using LgsImpact.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace LgsImpact.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController(ICosmosDbService cosmos, ITokenService tokenService, IAuditService audit) : ControllerBase
{
    public record LoginRequest(string Email, string Password);
    public record LoginResponse(string Token, int AdminId, string Email, string Name);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var email = req.Email.Trim().ToLowerInvariant();
        if (!email.Contains('@')) email += "@lgs.local";

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var ua = HttpContext.Request.Headers.UserAgent.ToString();

        var admin = await cosmos.GetAdminByEmailAsync(email);
        if (admin is null || !BCrypt.Net.BCrypt.Verify(req.Password, admin.PasswordHash))
        {
            await audit.LogAsync(0, email, AuditEventType.Error, entityType: "Auth",
                details: $"Failed login attempt for: {email}", ip: ip, userAgent: ua);
            return Unauthorized(new { message = "Invalid credentials" });
        }

        admin.LastLogin = DateTime.UtcNow.ToString("o");
        await cosmos.UpsertAdminAsync(admin);

        var token = tokenService.GenerateToken(admin);
        await audit.LogAsync(admin.AdminId, admin.Email, AuditEventType.Login,
            entityType: "Auth", entityId: admin.AdminId.ToString(),
            details: "Successful login", ip: ip, userAgent: ua);

        return Ok(new LoginResponse(token, admin.AdminId, admin.Email, admin.Name));
    }
}
