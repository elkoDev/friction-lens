using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Official public Apple App Store iTunes RSS JSON customer reviews connector (zero auth).
/// Target format: "us|123456789" (country|appId) or just "123456789" (defaults to "us").
/// </summary>
public sealed class AppStoreReviewConnector(
    IHttpClientFactory httpClientFactory,
    ConnectorRateLimiter rateLimiter,
    ILogger<AppStoreReviewConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.AppStoreReviews;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var parts = context.Source.Target.Split('|', 2, StringSplitOptions.TrimEntries);
        var country = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0].ToLowerInvariant() : "us";
        var appId = (parts.Length == 2 ? parts[1] : parts[0]).Trim();

        if (!appId.All(char.IsDigit) || string.IsNullOrEmpty(appId))
        {
            throw new InvalidOperationException("AppStoreReviews target must be a numeric App ID (e.g., 'us|123456789').");
        }

        var url = $"https://itunes.apple.com/{country}/rss/customerreviews/page=1/id={appId}/sortby=mostrecent/json";
        using var lease = await rateLimiter.AcquireAsync(SourceType.AppStoreReviews, "itunes.apple.com", ct);

        var client = httpClientFactory.CreateClient("IngestionClient");
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<FetchedDocument>();
        var maxTimestamp = context.Checkpoint?.CursorTimestamp;

        if (doc.RootElement.TryGetProperty("feed", out var feed)
            && feed.TryGetProperty("entry", out var entries)
            && entries.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in entries.EnumerateArray().Take(Math.Clamp(context.MaxItems, 5, 50)))
            {
                var id = GetLabel(entry, "id");
                var title = GetLabel(entry, "title");
                var body = GetLabel(entry, "content");
                var rating = GetLabel(entry, "im:rating");
                var updatedStr = GetLabel(entry, "updated");
                var publishedAt = DateTimeOffset.TryParse(updatedStr, out var dt) ? dt : DateTimeOffset.UtcNow;

                if (string.IsNullOrWhiteSpace(body) || body.Length < 30) continue;
                if (context.Checkpoint?.CursorTimestamp is DateTimeOffset since && publishedAt <= since) continue;

                results.Add(new FetchedDocument(
                    SourceType.AppStoreReviews,
                    $"appstore-{appId}-{id}",
                    $"[{rating}★] {title}",
                    body.Trim(),
                    $"https://apps.apple.com/{country}/app/id{appId}",
                    publishedAt));

                if (maxTimestamp is null || publishedAt > maxTimestamp) maxTimestamp = publishedAt;
            }
        }

        logger.LogInformation("AppStoreReviewConnector fetched {Count} reviews for app {AppId}", results.Count, appId);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.FirstOrDefault()?.ExternalId, null, null));
    }

    private static string GetLabel(JsonElement obj, string prop)
    {
        if (obj.TryGetProperty(prop, out var el) && el.TryGetProperty("label", out var lbl))
        {
            return lbl.GetString() ?? string.Empty;
        }
        return string.Empty;
    }
}
