using System.Text;
using System.Text.Json;

namespace LgsImpact.Api.Services;

// Legacy alias — keeps AiController compiling without changes until it is updated to ILlmProvider
public interface ILlmService : ILlmProvider { }

/// <summary>
/// Generic Ollama provider — for local development with any model.
/// Configure via Ollama:BaseUrl and Ollama:Model in appsettings.json.
/// </summary>
public class OllamaProvider(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string ModelName => config["Ollama:Model"] ?? "llama3.2";

    public async Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default)
    {
        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";

        var body = new
        {
            model = ModelName,
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

/// <summary>
/// Meta Llama production provider — calls an Ollama instance serving a Meta Llama model.
/// Configure via LlmProvider:BaseUrl (default same as Ollama) and LlmProvider:Model (default meta-llama/Meta-Llama-3.1-8B-Instruct).
/// BRD section 10.2: Gemini must not reach production; this provider satisfies that requirement.
/// </summary>
public class MetaLlamaProvider(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string ModelName => config["LlmProvider:Model"] ?? "llama3.1";

    public async Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default)
    {
        var baseUrl = config["LlmProvider:BaseUrl"]
            ?? config["Ollama:BaseUrl"]
            ?? "http://localhost:11434";

        var body = new
        {
            model = ModelName,
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
