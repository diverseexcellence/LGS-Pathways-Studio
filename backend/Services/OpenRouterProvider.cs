using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace LgsImpact.Api.Services;

/// <summary>
/// OpenRouter provider — free-tier hosted inference, OpenAI-compatible API.
/// Aggregates many free models: Meta Llama, Mistral, Qwen, DeepSeek, etc.
/// No self-hosted infra required.
///
/// Config:
///   LlmProvider:Name    = "openrouter"
///   LlmProvider:ApiKey  = "<your-openrouter-api-key>"   (also readable from env OPENROUTER_API_KEY)
///   LlmProvider:Model   = "meta-llama/llama-3.1-8b-instruct:free"  (default)
///
/// Other free models: "mistralai/mistral-7b-instruct:free", "qwen/qwen-2-7b-instruct:free"
/// Get a free API key at https://openrouter.ai/keys
/// </summary>
public class OpenRouterProvider(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private const string BaseUrl = "https://openrouter.ai/api/v1/chat/completions";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string ModelName => config["LlmProvider:Model"] ?? "meta-llama/llama-3.1-8b-instruct:free";

    private string ApiKey =>
        config["LlmProvider:ApiKey"]
        ?? Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")
        ?? throw new InvalidOperationException("OpenRouter API key not configured. Set LlmProvider:ApiKey or OPENROUTER_API_KEY.");

    public async Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default)
    {
        var body = new
        {
            model = ModelName,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            temperature = 0.3,
            max_tokens = 512
        };

        using var client = httpFactory.CreateClient("llm");
        using var req = new HttpRequestMessage(HttpMethod.Post, BaseUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
        req.Headers.Add("HTTP-Referer", "https://lgs-impact.azurewebsites.net");
        req.Headers.Add("X-Title", "LGS Impact");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        using var res = await client.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);

        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString()
            ?? "Summary unavailable.";
    }
}
