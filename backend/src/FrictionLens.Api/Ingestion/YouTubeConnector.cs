using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Official YouTube Data API v3 connector (free 10,000 units/day quota).
/// Target formats: "video:{videoId}" (1 unit) or "search:{query}" (100 units + 1/video).
/// Gracefully skips with a descriptive warning when YOUTUBE_API_KEY is not configured.
/// </summary>
public sealed class YouTubeConnector(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ConnectorRateLimiter rateLimiter,
    ILogger<YouTubeConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.YouTube;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var apiKey = configuration["Connectors:YouTubeApiKey"] ?? Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");
        var prevCheckpoint = context.Checkpoint ?? new SyncCheckpoint(null, null, null, null);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogInformation("YouTubeConnector skipped: YOUTUBE_API_KEY not configured.");
            return new ConnectorFetchResult(
                [],
                prevCheckpoint,
                Warning: "YouTube connector skipped because Connectors:YouTubeApiKey / YOUTUBE_API_KEY is not configured.");
        }

        var target = context.Source.Target.Trim();
        var videoIds = new List<(string VideoId, string Title)>();
        var client = httpClientFactory.CreateClient("IngestionClient");

        if (target.StartsWith("video:", StringComparison.OrdinalIgnoreCase))
        {
            var vid = target["video:".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(vid)) videoIds.Add((vid, $"YouTube Video {vid}"));
        }
        else
        {
            var query = target.StartsWith("search:", StringComparison.OrdinalIgnoreCase)
                ? target["search:".Length..].Trim()
                : target;

            using var searchLease = await rateLimiter.AcquireAsync(SourceType.YouTube, "www.googleapis.com", ct);
            var searchUrl = $"https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=3&q={Uri.EscapeDataString(query)}&key={Uri.EscapeDataString(apiKey)}";
            using var sResp = await client.GetAsync(searchUrl, ct);
            sResp.EnsureSuccessStatusCode();

            await using var sStream = await sResp.Content.ReadAsStreamAsync(ct);
            using var sDoc = await JsonDocument.ParseAsync(sStream, cancellationToken: ct);

            if (sDoc.RootElement.TryGetProperty("items", out var sItems))
            {
                foreach (var item in sItems.EnumerateArray())
                {
                    var vid = item.GetProperty("id").GetProperty("videoId").GetString();
                    var title = item.GetProperty("snippet").GetProperty("title").GetString() ?? "YouTube Discussion";
                    if (!string.IsNullOrWhiteSpace(vid)) videoIds.Add((vid, title));
                }
            }
        }

        var results = new List<FetchedDocument>();
        var maxTimestamp = prevCheckpoint.CursorTimestamp;
        var perVideoLimit = Math.Clamp(context.MaxItems / Math.Max(videoIds.Count, 1), 5, 30);

        foreach (var (videoId, title) in videoIds)
        {
            using var commentLease = await rateLimiter.AcquireAsync(SourceType.YouTube, "www.googleapis.com", ct);
            var url = $"https://www.googleapis.com/youtube/v3/commentThreads?part=snippet&order=time&textFormat=plainText&maxResults={perVideoLimit}&videoId={Uri.EscapeDataString(videoId)}&key={Uri.EscapeDataString(apiKey)}";
            using var resp = await client.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            if (!doc.RootElement.TryGetProperty("items", out var items)) continue;

            foreach (var item in items.EnumerateArray())
            {
                var id = item.GetProperty("id").GetString() ?? "";
                var snippet = item.GetProperty("snippet").GetProperty("topLevelComment").GetProperty("snippet");
                var text = snippet.TryGetProperty("textDisplay", out var txtEl) ? txtEl.GetString() ?? "" : "";
                var publishedAt = snippet.TryGetProperty("publishedAt", out var dEl) && DateTimeOffset.TryParse(dEl.GetString(), out var dt)
                    ? dt
                    : DateTimeOffset.UtcNow;

                if (prevCheckpoint.CursorTimestamp.HasValue && publishedAt <= prevCheckpoint.CursorTimestamp.Value)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(text) || text.Length < 35) continue;

                results.Add(new FetchedDocument(
                    SourceType.YouTube,
                    $"yt-{id}",
                    title,
                    text.Trim(),
                    $"https://www.youtube.com/watch?v={videoId}&lc={id}",
                    publishedAt));

                if (maxTimestamp is null || publishedAt > maxTimestamp) maxTimestamp = publishedAt;
            }
        }

        logger.LogInformation("YouTubeConnector fetched {Count} comments across {Videos} video(s).", results.Count, videoIds.Count);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.FirstOrDefault()?.ExternalId, null, null));
    }
}
