using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Free, zero-auth Hacker News connector backed by the official Algolia HN Search API
/// with incremental created_at_i cursoring and polite rate limiting.
/// </summary>
public sealed partial class HackerNewsConnector(
    IHttpClientFactory httpClientFactory,
    ConnectorRateLimiter rateLimiter,
    ILogger<HackerNewsConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.HackerNews;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(context.Source.Target)
            ? string.Join(" OR ", context.Entities.Select(e => e.Name))
            : context.Source.Target;

        var numericFilter = context.Checkpoint?.CursorTimestamp is DateTimeOffset since
            ? $"&numericFilters=created_at_i>{since.ToUnixTimeSeconds()}"
            : string.Empty;

        var url = $"https://hn.algolia.com/api/v1/search_by_date?query={Uri.EscapeDataString(query)}&tags=(comment,story)&hitsPerPage={Math.Clamp(context.MaxItems, 5, 50)}{numericFilter}";

        using var lease = await rateLimiter.AcquireAsync(SourceType.HackerNews, "hn.algolia.com", ct);

        var client = httpClientFactory.CreateClient("IngestionClient");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<FetchedDocument>();
        var maxTimestamp = context.Checkpoint?.CursorTimestamp;

        if (doc.RootElement.TryGetProperty("hits", out var hits))
        {
            foreach (var hit in hits.EnumerateArray())
            {
                var objectId = hit.TryGetProperty("objectID", out var idEl) ? idEl.GetString() ?? "" : "";
                var rawComment = hit.TryGetProperty("comment_text", out var txtEl) && txtEl.ValueKind == JsonValueKind.String
                    ? txtEl.GetString() ?? ""
                    : (hit.TryGetProperty("story_text", out var stEl) && stEl.ValueKind == JsonValueKind.String ? stEl.GetString() ?? "" : "");
                var storyTitle = hit.TryGetProperty("story_title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                    ? titleEl.GetString() ?? "Hacker News Discussion"
                    : (hit.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String ? tEl.GetString() ?? "Hacker News Discussion" : "Hacker News Discussion");

                var createdAt = hit.TryGetProperty("created_at", out var dateEl) && DateTimeOffset.TryParse(dateEl.GetString(), out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;

                var cleaned = StripHtml(rawComment);
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 40) continue;

                results.Add(new FetchedDocument(
                    SourceType.HackerNews,
                    $"hn-{objectId}",
                    storyTitle,
                    cleaned,
                    $"https://news.ycombinator.com/item?id={objectId}",
                    createdAt));

                if (maxTimestamp is null || createdAt > maxTimestamp) maxTimestamp = createdAt;
            }
        }

        logger.LogInformation("HackerNewsConnector fetched {Count} items for query '{Query}'", results.Count, query);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.FirstOrDefault()?.ExternalId, null, null));
    }

    private static string StripHtml(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var decoded = WebUtility.HtmlDecode(input);
        var noTags = HtmlTagRegex().Replace(decoded, " ");
        return MultiSpaceRegex().Replace(noTags, " ").Trim();
    }

    [GeneratedRegex("<.*?>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
