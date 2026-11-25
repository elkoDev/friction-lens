using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Compliant GitHub Public Issues Search connector with ETag 304 caching,
/// rate-limit awareness, and optional GITHUB_TOKEN auth.
/// </summary>
public sealed class GitHubConnector(
    IHttpClientFactory httpClientFactory,
    IConfiguration configuration,
    ConnectorRateLimiter rateLimiter,
    ILogger<GitHubConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.GitHub;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var query = string.IsNullOrWhiteSpace(context.Source.Target)
            ? $"{string.Join(" ", context.Entities.Take(2).Select(e => e.Name))} is:issue"
            : context.Source.Target;

        if (context.Checkpoint?.CursorTimestamp is DateTimeOffset since && !query.Contains("updated:", StringComparison.OrdinalIgnoreCase))
        {
            query = $"{query} updated:>={since:yyyy-MM-dd}";
        }

        var url = $"https://api.github.com/search/issues?q={Uri.EscapeDataString(query)}&sort=updated&order=desc&per_page={Math.Clamp(context.MaxItems, 5, 30)}";

        using var lease = await rateLimiter.AcquireAsync(SourceType.GitHub, "api.github.com", ct);

        var client = httpClientFactory.CreateClient("IngestionClient");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

        if (!string.IsNullOrWhiteSpace(context.Checkpoint?.LastEtag))
        {
            req.Headers.IfNoneMatch.TryParseAdd(context.Checkpoint.LastEtag);
        }

        var token = configuration["Connectors:GitHubToken"] ?? Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(req, ct);
        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new ConnectorFetchResult([], context.Checkpoint ?? new SyncCheckpoint(null, null, null, null), NotModified: true);
        }

        response.EnsureSuccessStatusCode();
        var nextEtag = response.Headers.ETag?.Tag ?? context.Checkpoint?.LastEtag;

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<FetchedDocument>();
        var maxTimestamp = context.Checkpoint?.CursorTimestamp;

        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                if (!item.TryGetProperty("id", out var idEl)) continue;
                var id = idEl.GetInt64().ToString();
                var title = item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "";
                var body = item.TryGetProperty("body", out var bEl) && bEl.ValueKind == JsonValueKind.String ? bEl.GetString() ?? "" : "";
                var htmlUrl = item.TryGetProperty("html_url", out var uEl) ? uEl.GetString() ?? "" : "";
                var createdAt = item.TryGetProperty("created_at", out var dEl) && DateTimeOffset.TryParse(dEl.GetString(), out var dt)
                    ? dt
                    : DateTimeOffset.UtcNow;

                var combined = $"{title}\n{body}".Trim();
                if (combined.Length < 40) continue;
                if (combined.Length > 3000) combined = combined[..3000];

                results.Add(new FetchedDocument(
                    SourceType.GitHub,
                    $"gh-{id}",
                    title,
                    combined,
                    htmlUrl,
                    createdAt));

                if (maxTimestamp is null || createdAt > maxTimestamp) maxTimestamp = createdAt;
            }
        }

        logger.LogInformation("GitHubConnector fetched {Count} issues for query '{Query}'", results.Count, query);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.FirstOrDefault()?.ExternalId, null, nextEtag));
    }
}
