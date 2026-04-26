using LgsImpact.Api.Models;

namespace LgsImpact.Api.Services;

public enum AuditEventType { Login, Upload, View, Edit, Export, AI, Delete, Error }

public interface IAuditService
{
    Task LogAsync(int adminId, string adminEmail, AuditEventType eventType,
        string? entityType = null, string? entityId = null,
        string? details = null, string? ip = null, string? userAgent = null);
}

public class AuditService(ICosmosDbService cosmos) : IAuditService
{
    public async Task LogAsync(int adminId, string adminEmail, AuditEventType eventType,
        string? entityType = null, string? entityId = null,
        string? details = null, string? ip = null, string? userAgent = null)
    {
        var log = new AuditLogDocument
        {
            Id          = Guid.NewGuid().ToString(),
            AdminId     = adminId,
            AdminEmail  = adminEmail,
            EventType   = eventType.ToString(),
            EntityType  = entityType,
            EntityId    = entityId,
            Details     = details,
            Timestamp   = DateTime.UtcNow.ToString("o"),
            IpAddress   = ip,
            UserAgent   = userAgent
        };

        try { await cosmos.CreateAuditLogAsync(log); }
        catch { /* never let audit failure break the request */ }
    }
}
