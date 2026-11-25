using System.Net.Http.Json;
using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

/// <summary>
/// Zero-cost local LLM provider using Ollama's JSON structured output API.
/// Captures token counts and supports strict enum validation + 1-shot repair instructions.
/// </summary>
public sealed class OllamaExtractionProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OllamaExtractionProvider> logger) : IExtractionProvider
{
    public string ProviderName => "Ollama";
    public string ModelName => configuration["Extraction:OllamaModel"] ?? "qwen2.5:7b";

    public async Task<ExtractionResult?> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var baseUrl = (configuration["Extraction:OllamaBaseUrl"] ?? "http://localhost:11434").TrimEnd('/');
        var prompt = LlmPromptBuilder.BuildPrompt(context);

        var payload = new
        {
            model = ModelName,
            format = "json",
            stream = false,
            options = new { temperature = 0.1 },
            messages = new[]
            {
                new { role = "system", content = LlmPromptBuilder.SystemPrompt },
                new { role = "user", content = prompt }
            }
        };

        var client = httpClientFactory.CreateClient("LlmClient");
        using var response = await client.PostAsJsonAsync($"{baseUrl}/api/chat", payload, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var content = json.GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return null;

        var promptTokens = json.TryGetProperty("prompt_eval_count", out var pt) ? pt.GetInt32() : 0;
        var completionTokens = json.TryGetProperty("eval_count", out var ctEl) ? ctEl.GetInt32() : 0;

        return LlmPromptBuilder.ParseResponse(content, ModelName, promptTokens, completionTokens, logger);
    }
}
