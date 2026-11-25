using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

/// <summary>
/// Official Stack Exchange API v2.3 connector (zero-auth 300 req/day or 10k/day with optional key).
/// Target format: "stackoverflow|query" or "site|query" (defaults to stackoverflow).
/// </summary>
public sealed partial class StackExchangeConnector(
    IHttpClientFactory httpClientFactory,
    ConnectorRateLimiter rateLimiter,
    ILogger<StackExchangeConnector> logger) : ISourceConnector
{
    public SourceType SupportedSource => SourceType.StackExchange;

    public async Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct)
    {
        var parts = context.Source.Target.Split('|', 2, StringSplitOptions.TrimEntries);
        var site = parts.Length == 2 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : "stackoverflow";
        var query = parts.Length == 2 ? parts[1] : parts[0];
        if (string.IsNullOrWhiteSpace(query))
        {
            query = string.Join(" ", context.Entities.Take(2).Select(e => e.Name));
        }

        var fromDate = context.Checkpoint?.CursorTimestamp is DateTimeOffset since
            ? $"&fromdate={since.ToUnixTimeSeconds()}"
            : string.Empty;

        var url = $"https://api.stackexchange.com/2.3/search/advanced?site={Uri.EscapeDataString(site)}&q={Uri.EscapeDataString(query)}&sort=creation&order=desc&pagesize={Math.Clamp(context.MaxItems, 5, 30)}&filter=withbody{fromDate}";

        using var lease = await rateLimiter.AcquireAsync(SourceType.StackExchange, "api.stackexchange.com", ct);

        var client = httpClientFactory.CreateClient("GzipIngestionClient");
        using var resp = await client.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var results = new List<FetchedDocument>();
        var maxTimestamp = context.Checkpoint?.CursorTimestamp;

        if (doc.RootElement.TryGetProperty("items", out var items))
        {
            foreach (var item in items.EnumerateArray())
            {
                var qid = item.TryGetProperty("question_id", out var idEl) ? idEl.GetInt64() : 0;
                var title = WebUtility.HtmlDecode(item.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "" : "");
                var rawBody = item.TryGetProperty("body", out var bEl) ? bEl.GetString() ?? "" : "";
                var link = item.TryGetProperty("link", out var lEl) ? lEl.GetString() ?? "" : "";
                var created = item.TryGetProperty("creation_date", out var dEl)
                    ? DateTimeOffset.FromUnixTimeSeconds(dEl.GetInt64())
                    : DateTimeOffset.UtcNow;

                var body = StripHtml(rawBody);
                if (body.Length < 40) continue;
                if (body.Length > 2500) body = body[..2500];

                results.Add(new FetchedDocument(
                    SourceType.StackExchange,
                    $"se-{site}-{qid}",
                    title,
                    body,
                    link,
                    created));

                if (maxTimestamp is null || created > maxTimestamp) maxTimestamp = created;
            }
        }

        logger.LogInformation("StackExchangeConnector fetched {Count} questions from '{Site}'", results.Count, site);
        return new ConnectorFetchResult(
            results,
            new SyncCheckpoint(maxTimestamp, results.FirstOrDefault()?.ExternalId, null, null));
    }

    private static string StripHtml(string input)
    {
        var decoded = WebUtility.HtmlDecode(input);
        var noTags = HtmlTagRegex().Replace(decoded, " ");
        return MultiSpaceRegex().Replace(noTags, " ").Trim();
    }

    [GeneratedRegex("<.*?>", RegexOptions.Compiled)]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
