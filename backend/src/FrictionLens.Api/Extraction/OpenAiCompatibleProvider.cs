using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

/// <summary>
/// Cloud-ready OpenAI-compatible provider (OpenAI, Gemini OpenAI endpoint, OpenRouter, Groq, vLLM).
/// Captures prompt/completion token usage for UsageLedger and enforces strict enum validation.
/// </summary>
public sealed class OpenAiCompatibleProvider(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ILogger<OpenAiCompatibleProvider> logger) : IExtractionProvider
{
    public string ProviderName => "OpenAiCompatible";
    public string ModelName => configuration["Extraction:CloudModel"] ?? "gpt-4o-mini";

    public async Task<ExtractionResult?> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var apiKey = configuration["Extraction:ApiKey"] ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException("Extraction:ApiKey (or LLM_API_KEY env var) is required for OpenAiCompatible provider.");
        }

        var baseUrl = (configuration["Extraction:CloudBaseUrl"] ?? "https://api.openai.com/v1").TrimEnd('/');
        var prompt = LlmPromptBuilder.BuildPrompt(context);

        var payload = new
        {
            model = ModelName,
            temperature = 0.1,
            response_format = new { type = "json_object" },
            messages = new[]
            {
                new { role = "system", content = LlmPromptBuilder.SystemPrompt },
                new { role = "user", content = prompt }
            }
        };

        var client = httpClientFactory.CreateClient("LlmClient");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = JsonContent.Create(payload);

        using var response = await client.SendAsync(req, ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        var content = json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)) return null;

        var promptTokens = 0;
        var completionTokens = 0;
        if (json.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ctEl)) completionTokens = ctEl.GetInt32();
        }

        return LlmPromptBuilder.ParseResponse(content, ModelName, promptTokens, completionTokens, logger);
    }
}

public static class LlmPromptBuilder
{
    public const string SystemPrompt = """
        You are a precision Product & Competitive Decision Intelligence extractor.
        Analyze a user post and extract ONLY verifiable, structured buying/switching decisions, product friction, and unmet requirements.
        NEVER invent quotes. The "verbatimEvidence" field MUST be an exact contiguous substring copied directly from the input text.
        Reuse an existing canonicalTheme slug whenever semantically equivalent.
        Respond strictly with valid JSON matching this schema:
        {
          "isActionable": true,
          "primaryEntity": "Entity Name",
          "competitorsCompared": ["Competitor Name"],
          "outcome": "ChosePrimary | RejectedPrimary | SwitchedAway | Evaluating | Workaround",
          "summary": "One crisp executive sentence",
          "unmetRequirement": "Concrete product or service requirement inferred from the post",
          "verbatimEvidence": "Exact verbatim quote copied from the text",
          "confidence": 0.9,
          "drivers": [
            {
              "targetEntity": "Entity Name",
              "category": "FeatureGap | ServiceSupport | LogisticsAccess | PricingTco | ReliabilityQuality | EcosystemLockin | DeveloperExperience | Other",
              "canonicalTheme": "snake_case_theme",
              "displayLabel": "Human Readable Theme",
              "rawPhrase": "short phrase from text",
              "severity": "DealBreaker | MajorFriction | MinorAnnoyance",
              "polarity": "Advantage | Drawback"
            }
          ]
        }
        """;

    public static string BuildPrompt(ExtractionContext context)
    {
        var entityList = string.Join(", ", context.Entities.Select(e =>
            string.IsNullOrWhiteSpace(e.AliasesCsv) ? e.Name : $"{e.Name} (aliases: {e.AliasesCsv})"));

        var themeBlock = context.ExistingCanonicalThemes.Count > 0
            ? $"\nExisting Canonical Themes (reuse when equivalent): {string.Join(", ", context.ExistingCanonicalThemes.Take(40))}"
            : string.Empty;

        var repairBlock = !string.IsNullOrWhiteSpace(context.RepairInstruction)
            ? $"\nIMPORTANT CORRECTION: {context.RepairInstruction}"
            : string.Empty;

        return $"""
            Domain: {context.WorkspaceDomain}
            Tracked Entities: {entityList}{themeBlock}{repairBlock}
            Title: {context.Title}
            Post Text:
            {context.Body}
            """;
    }

    public static ExtractionResult? ParseResponse(
        string rawJson,
        string modelName,
        int promptTokens,
        int completionTokens,
        ILogger? logger = null)
    {
        try
        {
            var opts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var dto = JsonSerializer.Deserialize<LlmWireResponse>(rawJson, opts);
            if (dto is null || !dto.IsActionable || string.IsNullOrWhiteSpace(dto.PrimaryEntity))
            {
                return null;
            }

            // Strict enum validation — never silently default invalid enum strings to 0!
            if (string.IsNullOrWhiteSpace(dto.Outcome) || !Enum.TryParse<DecisionOutcome>(dto.Outcome, ignoreCase: true, out var outcome))
            {
                logger?.LogWarning("Rejected LLM extraction with invalid Outcome enum '{Outcome}'", dto.Outcome);
                return null;
            }

            var drivers = new List<ExtractedDriver>();
            foreach (var d in dto.Drivers ?? [])
            {
                if (!Enum.TryParse<DriverCategory>(d.Category, ignoreCase: true, out var cat))
                    cat = DriverCategory.Other;
                if (!Enum.TryParse<DriverSeverity>(d.Severity, ignoreCase: true, out var sev))
                    continue; // Skip malformed driver severity
                if (!Enum.TryParse<DriverPolarity>(d.Polarity, ignoreCase: true, out var pol))
                    continue; // Skip malformed driver polarity

                drivers.Add(new ExtractedDriver(
                    d.TargetEntity ?? dto.PrimaryEntity,
                    cat,
                    CanonicalTaxonomyResolver.NormalizeSlug(d.CanonicalTheme ?? "general_friction"),
                    string.IsNullOrWhiteSpace(d.DisplayLabel) ? "General Friction" : d.DisplayLabel.Trim(),
                    d.RawPhrase ?? string.Empty,
                    sev,
                    pol));
            }

            return new ExtractionResult(
                true,
                dto.PrimaryEntity.Trim(),
                dto.CompetitorsCompared ?? [],
                outcome,
                dto.Summary?.Trim() ?? string.Empty,
                dto.UnmetRequirement?.Trim() ?? string.Empty,
                dto.VerbatimEvidence?.Trim() ?? string.Empty,
                Math.Clamp(dto.Confidence, 0.1, 1.0),
                modelName,
                drivers,
                promptTokens,
                completionTokens);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to parse structured LLM extraction JSON.");
            return null;
        }
    }

    private sealed record LlmWireDriver(
        string? TargetEntity,
        string? Category,
        string? CanonicalTheme,
        string? DisplayLabel,
        string? RawPhrase,
        string? Severity,
        string? Polarity);

    private sealed record LlmWireResponse(
        bool IsActionable,
        string? PrimaryEntity,
        List<string>? CompetitorsCompared,
        string? Outcome,
        string? Summary,
        string? UnmetRequirement,
        string? VerbatimEvidence,
        double Confidence,
        List<LlmWireDriver>? Drivers);
}
