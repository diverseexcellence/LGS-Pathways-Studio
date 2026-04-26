using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LgsImpact.Api.Services;

public interface ILlmService
{
    Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default);
}

/// <summary>
/// Calls a locally-running Ollama instance (free, no API key needed).
/// Default model: llama3.2 — change Ollama:Model in appsettings.json.
/// Run locally: ollama run llama3.2
/// </summary>
public class OllamaService(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default)
    {
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        var model = config["Ollama:Model"] ?? "llama3.2";

        var body = new
        {
            model,
            prompt,
            stream = false,
            options = new { temperature = 0.3, num_predict = 512 }
        };

        using var client = httpFactory.CreateClient("ollama");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/api/generate");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("response").GetString()
            ?? "Summary unavailable.";
    }
}
