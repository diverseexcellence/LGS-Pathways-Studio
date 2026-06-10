using System.Text;
using System.Text.Json;

namespace LgsImpact.Api.Services;

/// <summary>
/// Groq cloud provider — free-tier hosted inference, OpenAI-compatible API.
/// Serves Meta Llama models (llama-3.1-8b-instant, llama-3.3-70b-versatile, etc.)
/// without any self-hosted infrastructure. No Ollama required.
///
/// Config:
///   LlmProvider:Name    = "groq"
///   LlmProvider:ApiKey  = "<your-groq-api-key>"   (also readable from env GROQ_API_KEY)
///   LlmProvider:Model   = "llama-3.1-8b-instant"  (default)
///
/// Get a free API key at https://console.groq.com
/// </summary>
public class GroqProvider(IConfiguration config, IHttpClientFactory httpFactory) : ILlmService
{
    private const string BaseUrl = "https://api.groq.com/openai/v1/chat/completions";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public string ModelName => config["LlmProvider:Model"] ?? "llama-3.1-8b-instant";

    private string ApiKey =>
        config["LlmProvider:ApiKey"]
        ?? Environment.GetEnvironmentVariable("GROQ_API_KEY")
        ?? throw new InvalidOperationException("Groq API key not configured. Set LlmProvider:ApiKey or GROQ_API_KEY.");

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
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ApiKey);
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
