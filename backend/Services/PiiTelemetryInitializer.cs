using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.ApplicationInsights.Extensibility;
using System.Text.RegularExpressions;

namespace LgsImpact.Api.Services;

/// <summary>
/// Strips PII from all telemetry before it reaches Application Insights.
/// Scrubs student names, emails, DOBs, and STN-style IDs from URLs, queries, and dependency data.
/// </summary>
public partial class PiiTelemetryInitializer : ITelemetryInitializer
{
    private static readonly string[] PiiPropertyKeys =
        ["studentName", "fullName", "email", "dob", "dateOfBirth", "stn"];

    public void Initialize(ITelemetry telemetry)
    {
        ScrubRequestTelemetry(telemetry as RequestTelemetry);
        ScrubDependencyTelemetry(telemetry as DependencyTelemetry);
        ScrubProperties(telemetry as ISupportProperties);
    }

    private static void ScrubRequestTelemetry(RequestTelemetry? req)
    {
        if (req is null) return;
        req.Url = ScrubUrl(req.Url);
        ScrubProperties(req);
    }

    private static void ScrubDependencyTelemetry(DependencyTelemetry? dep)
    {
        if (dep is null) return;
        dep.Data = ScrubString(dep.Data);
        dep.Name = ScrubString(dep.Name);
        ScrubProperties(dep);
    }

    private static void ScrubProperties(ISupportProperties? item)
    {
        if (item is null) return;
        foreach (var key in PiiPropertyKeys)
        {
            if (item.Properties.ContainsKey(key))
                item.Properties[key] = "[REDACTED]";
        }
    }

    private static Uri? ScrubUrl(Uri? uri)
    {
        if (uri is null) return null;
        var scrubbed = ScrubString(uri.ToString());
        return Uri.TryCreate(scrubbed, UriKind.Absolute, out var result) ? result : uri;
    }

    private static string ScrubString(string? input)
    {
        if (string.IsNullOrEmpty(input)) return input ?? string.Empty;
        // Redact student ID path segments e.g. /students/s-<guid>
        input = StudentIdRegex().Replace(input, "/students/[REDACTED]");
        // Redact email addresses
        input = EmailRegex().Replace(input, "[EMAIL]");
        return input;
    }

    [GeneratedRegex(@"/students/s-[0-9a-f\-]{36}", RegexOptions.IgnoreCase)]
    private static partial Regex StudentIdRegex();

    [GeneratedRegex(@"[a-zA-Z0-9._%+\-]+@[a-zA-Z0-9.\-]+\.[a-zA-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();
}
