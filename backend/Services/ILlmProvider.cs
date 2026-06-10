namespace LgsImpact.Api.Services;

/// <summary>
/// Pluggable LLM provider abstraction. Swap implementations via LlmProvider:Name in config.
/// Supported values: "ollama" (local dev), "meta-llama" (production via Ollama serving Llama).
/// </summary>
public interface ILlmProvider
{
    string ModelName { get; }
    Task<string> GenerateSummaryAsync(string prompt, CancellationToken ct = default);
}
