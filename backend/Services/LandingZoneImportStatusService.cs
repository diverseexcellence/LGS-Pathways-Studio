namespace LgsImpact.Api.Services;

/// <summary>
/// Tracks the state of the background landing-zone import job. The import used to block the HTTP
/// request for the whole batch — with a couple dozen files and per-row Cosmos lookups for student
/// matching, that routinely exceeded Azure App Service's platform request timeout (~230s) and
/// the connection was reset mid-response, which surfaces to the browser as a JSON parse error even
/// though the import kept running server-side. The controller now kicks the work off in the
/// background and returns immediately; the frontend polls this status instead.
/// In-memory and single-instance is fine here — this is a manually-triggered admin operation, not
/// something that needs to survive an app restart.
/// </summary>
public interface ILandingZoneImportStatusService
{
    LandingZoneImportStatus Current { get; }
    bool TryStart();
    void Complete(string message, List<object> results);
    void Fail(string error);
}

public record LandingZoneImportStatus(
    string State, // "idle" | "running" | "completed" | "failed"
    string? StartedAt,
    string? CompletedAt,
    string? Message,
    List<object>? Results,
    string? Error);

public class LandingZoneImportStatusService : ILandingZoneImportStatusService
{
    private readonly object _lock = new();
    private LandingZoneImportStatus _current = new("idle", null, null, null, null, null);

    public LandingZoneImportStatus Current { get { lock (_lock) return _current; } }

    public bool TryStart()
    {
        lock (_lock)
        {
            if (_current.State == "running") return false;
            _current = new LandingZoneImportStatus("running", DateTime.UtcNow.ToString("o"), null, null, null, null);
            return true;
        }
    }

    public void Complete(string message, List<object> results)
    {
        lock (_lock)
        {
            _current = _current with { State = "completed", CompletedAt = DateTime.UtcNow.ToString("o"), Message = message, Results = results };
        }
    }

    public void Fail(string error)
    {
        lock (_lock)
        {
            _current = _current with { State = "failed", CompletedAt = DateTime.UtcNow.ToString("o"), Error = error };
        }
    }
}
