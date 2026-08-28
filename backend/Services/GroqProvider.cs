using System.Text;
using System.Text.Json;

namespace LgsImpact.Api.Services;

/// <summary>
/// Groq cloud provider — hosted inference, OpenAI-compatible API.
///
/// Config:
///   LlmProvider:Name    = "groq"
///   LlmProvider:ApiKey  = "&lt;your-groq-api-key&gt;"   (also readable from env GROQ_API_KEY)
///   LlmProvider:Model   = "openai/gpt-oss-20b"  (default)
///
/// Get a free API key at https://console.groq.com
/// Groq decommissioned llama-3.1-8b-instant for free/developer tiers on 2026-08-16
/// (replacement: openai/gpt-oss-20b). Retired IDs are remapped automatically.
/// </summary>
public class GroqProvider(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
    private const string DefaultModel = "openai/gpt-oss-20b";

    // Groq shutdown 2026-08-16 for free/developer tiers. Map to the documented replacement
    // so Azure App Settings that still name the old model keep working after deploy.
    private static readonly Dictionary<string, string> RetiredModelReplacements =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["llama-3.1-8b-instant"] = "openai/gpt-oss-20b",
            ["llama-3.3-70b-versatile"] = "openai/gpt-oss-120b",
        };

    public string ModelName => ResolveModel(config["LlmProvider:Model"]);

    private static string ResolveModel(string? configured)
    {
        var model = string.IsNullOrWhiteSpace(configured) ? DefaultModel : configured.Trim();
        return RetiredModelReplacements.TryGetValue(model, out var replacement) ? replacement : model;
    }

    private string ApiKey =>
        config["LlmProvider:ApiKey"]
        ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
        ?? throw new InvalidOperationException("Groq API key not configured. Set LlmProvider:ApiKey or GROQ_API_KEY.");

    // gpt-oss spends completion tokens on hidden reasoning first. 512 was often
    // exhausted before any visible summary, so Groq returned HTTP 200 with empty content.
    private const int MaxCompletionTokens = 2048;

    public async Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = ModelName,
            ["messages"] = new[] { new { role = "user", content = prompt } },
            ["temperature"] = 0.3,
            ["max_completion_tokens"] = MaxCompletionTokens,
        };
        // gpt-oss defaults to medium reasoning, which can consume the whole budget
        // before any visible summary. Low leaves room for the teacher-facing text.
        if (ModelName.Contains("gpt-oss", StringComparison.OrdinalIgnoreCase))
            body["reasoning_effort"] = "low";

        using var client = httpFactory.CreateClient("llm");
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req, ct);
        var json = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Groq {(int)res.StatusCode}: {ExtractGroqError(json)}");

        using var doc = JsonDocument.Parse(json);
        var choice = doc.RootElement.GetProperty("choices")[0];
        var message = choice.GetProperty("message");
        var content = message.TryGetProperty("content", out var contentEl)
            ? contentEl.GetString()
            : null;
        var finishReason = choice.TryGetProperty("finish_reason", out var fr)
            ? fr.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(content))
            throw new HttpRequestException(
                $"Groq returned empty content (finish_reason={finishReason ?? "unknown"}). " +
                "The reasoning model used the token budget before writing the summary.");

        return content;
    }

    private static string ExtractGroqError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.Object && err.TryGetProperty("message", out var msg))
                    return msg.GetString() ?? json;
                if (err.ValueKind == JsonValueKind.String)
                    return err.GetString() ?? json;
            }
        }
        catch (JsonException) { /* body was not JSON */ }
        return json.Length > 300 ? json[..300] : json;
    }
}
