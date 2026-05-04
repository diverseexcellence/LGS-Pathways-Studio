using LgsImpact.Api.Services;
using System.Security.Claims;

namespace LgsImpact.Api.Middleware;

// Auto-logs every request that touches a studentId in the URL path.
// Covers GET /api/students/{id}, GET /api/assessments/{id}, POST /api/export, POST /api/ai/{id}
public class PiiAuditMiddleware(RequestDelegate next)
{
    private static readonly string[] PiiRoutes = ["/api/students/", "/api/assessments/", "/api/export", "/api/ai/"];

    public async Task InvokeAsync(HttpContext context, IAuditService audit)
    {
        await next(context);

        var path = context.Request.Path.Value ?? "";
        if (!PiiRoutes.Any(r => path.Contains(r, StringComparison.OrdinalIgnoreCase))) return;
        if (context.Request.Method == "OPTIONS") return;

        var adminId = int.TryParse(context.User.FindFirstValue("adminId"), out var id) ? id : 0;
        var adminEmail = context.User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Email)
                         ?? context.User.FindFirstValue(ClaimTypes.Email)
                         ?? "anonymous";

        // Skip if already logged by the controller (login, upload, explicit audit calls)
        if (path.Contains("/api/audit") || path.Contains("/api/auth")) return;

        var eventType = context.Request.Method switch
        {
            "GET"    => AuditEventType.View,
            "POST"   => AuditEventType.Upload,
            "PATCH"  => AuditEventType.Edit,
            "PUT"    => AuditEventType.Edit,
            "DELETE" => AuditEventType.Delete,
            _        => AuditEventType.View
        };

        // Only log successful responses (2xx)
        if (context.Response.StatusCode < 200 || context.Response.StatusCode >= 300) return;

        await audit.LogAsync(
            adminId, adminEmail, eventType,
            entityType: "PiiAccess",
            entityId: path,
            details: $"{context.Request.Method} {path} → {context.Response.StatusCode}",
            ip: context.Connection.RemoteIpAddress?.ToString(),
            userAgent: context.Request.Headers.UserAgent.ToString()
        );
    }
}
