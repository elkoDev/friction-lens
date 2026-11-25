using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Hardened Discourse JSON API connector with SSRF protection, deterministic SHA-256 ExternalIds,
/// and per-host polite rate limiting.
/// Target format: "https://forum.example.com" or "https://forum.example.com|custom query"
/// </summary>
public sealed class DiscourseConnector(
    IHttpClientFactory httpClientFactory,
    ConnectorRateLimiter rateLimiter,
    ILogger<DiscourseConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.Discourse;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var parts = context.Source.Target.Split('|', 2, StringSplitOptions.TrimEntries);
        var rawBaseUrl = parts[0].TrimEnd('/');

        var (allowed, reason, normalizedUri) = await SsrfGuard.ValidateExternalUrlAsync(rawBaseUrl, ct);
        if (!allowed || normalizedUri is null)
        {
            throw new InvalidOperationException($"Discourse target rejected by SSRF guard: {reason}");
        }

        var query = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1])
            ? parts[1]
            : string.Join(" ", context.Entities.Take(2).Select(e => e.Name));

        if (context.Checkpoint?.CursorTimestamp is DateTimeOffset since)
        {
            query = $"{query} after:{since:yyyy-MM-dd}";
        }

        using var lease = await rateLimiter.AcquireAsync(SourceType.Discourse, normalizedUri.DnsSafeHost, ct);

        var url = $"{rawBaseUrl}/search.json?q={Uri.EscapeDataString(query)}";
        var client = httpClientFactory.CreateClient("IngestionClient");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var topicTitles = new Dictionary<long, (string Title, string Slug)>();
        if (doc.RootElement.TryGetProperty("topics", out var topics))
        {
            foreach (var t in topics.EnumerateArray())
            {
                if (t.TryGetProperty("id", out var idEl))
                {
                    var tid = idEl.GetInt64();
                    var title = t.TryGetProperty("title", out var tt) ? tt.GetString() ?? "" : "";
                    var slug = t.TryGetProperty("slug", out var sl) ? sl.GetString() ?? "" : "";
                    topicTitles[tid] = (title, slug);
                }
            }
        }

        // Deterministic cross-restart host hash (replaces non-deterministic string.GetHashCode())
        var hostHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBaseUrl.ToLowerInvariant())))[..10];
        var results = new List<FetchedDocument>();
        var maxTimestamp = context.Checkpoint?.CursorTimestamp;

        if (doc.RootElement.TryGetProperty("posts", out var posts))
        {
            foreach (var post in posts.EnumerateArray().Take(Math.Clamp(context.MaxItems, 5, 40)))
            {
                var id = post.TryGetProperty("id", out var idEl) ? idEl.GetInt64() : 0;
                var topicId = post.TryGetProperty("topic_id", out var tidEl) ? tidEl.GetInt64() : 0;
                var postNumber = post.TryGetProperty("post_number", out var pnEl) ? pnEl.GetInt32() : 1;
                var blurb = post.TryGetProperty("blurb", out var blEl) ? blEl.GetString() ?? "" : "";
                var createdAt = post.TryGetProperty("created_at", out var dEl) && DateTimeOffset.TryParse(dEl.GetString(), out var dt)
                    ? dt
                    : DateTimeOffset.UtcNow;

                if (string.IsNullOrWhiteSpace(blurb) || blurb.Length < 35) continue;

                var (title, slug) = topicTitles.TryGetValue(topicId, out var info)
                    ? info
                    : ("Community Forum Thread", "topic");

                results.Add(new FetchedDocument(
                    SourceType.Discourse,
                    $"discourse-{hostHash}-{id}",
                    title,
                    blurb,
                    $"{rawBaseUrl}/t/{slug}/{topicId}/{postNumber}",
                    createdAt));

                if (maxTimestamp is null || createdAt > maxTimestamp) maxTimestamp = createdAt;
            }
        }

        logger.LogInformation("DiscourseConnector fetched {Count} posts from {BaseUrl}", results.Count, rawBaseUrl);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.LastOrDefault()?.ExternalId, null, null));
    }
}
