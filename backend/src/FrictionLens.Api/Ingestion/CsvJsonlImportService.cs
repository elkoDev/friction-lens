using System.Text;
using System.Text.Json;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

public sealed record BulkParseResult(
    IReadOnlyList<FetchedDocument> Documents,
    int SkippedEmptyRows,
    int MalformedRows);

/// <summary>
/// Zero-dependency RFC-4180 CSV and JSONL bulk importer for first-party customer datasets
/// (reviews, support tickets, interview transcripts, survey exports).
/// </summary>
public static class CsvJsonlImportService
{
    private static readonly string[] BodyHeaderAliases = ["body", "text", "comment", "review", "content", "quote", "feedback", "message"];
    private static readonly string[] TitleHeaderAliases = ["title", "subject", "heading", "summary"];
    private static readonly string[] UrlHeaderAliases = ["url", "source_url", "sourceurl", "link", "permalink"];
    private static readonly string[] DateHeaderAliases = ["date", "published_at", "publishedat", "created_at", "timestamp"];

    public static BulkParseResult Parse(string rawContent, string format = "csv", int maxRows = 500)
    {
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return new BulkParseResult([], 0, 0);
        }

        return format.Equals("jsonl", StringComparison.OrdinalIgnoreCase)
            ? ParseJsonl(rawContent, maxRows)
            : ParseCsv(rawContent, maxRows);
    }

    private static BulkParseResult ParseJsonl(string rawContent, int maxRows)
    {
        var docs = new List<FetchedDocument>();
        int skipped = 0, malformed = 0;

        foreach (var rawLine in rawContent.Split('\n'))
        {
            if (docs.Count >= maxRows) break;
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line)) { skipped++; continue; }

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var body = TryGetString(root, BodyHeaderAliases);
                if (string.IsNullOrWhiteSpace(body)) { skipped++; continue; }

                var title = TryGetString(root, TitleHeaderAliases) ?? "JSONL Import";
                var url = TryGetString(root, UrlHeaderAliases) ?? string.Empty;
                var dateStr = TryGetString(root, DateHeaderAliases);
                var publishedAt = DateTimeOffset.TryParse(dateStr, out var dt) ? dt : DateTimeOffset.UtcNow;

                docs.Add(new FetchedDocument(
                    SourceType.JsonlImport,
                    $"jsonl-{Guid.NewGuid():N}",
                    title,
                    body,
                    url,
                    publishedAt));
            }
            catch (JsonException)
            {
                malformed++;
            }
        }

        return new BulkParseResult(docs, skipped, malformed);
    }

    private static BulkParseResult ParseCsv(string rawContent, int maxRows)
    {
        var rows = ReadCsvRows(rawContent);
        if (rows.Count == 0) return new BulkParseResult([], 0, 0);

        var headers = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        var bodyIdx = FindHeaderIndex(headers, BodyHeaderAliases);
        var titleIdx = FindHeaderIndex(headers, TitleHeaderAliases);
        var urlIdx = FindHeaderIndex(headers, UrlHeaderAliases);
        var dateIdx = FindHeaderIndex(headers, DateHeaderAliases);

        var startRow = 1;
        // If no recognizable header row exists, treat single-column or first-column as body starting at row 0
        if (bodyIdx < 0)
        {
            bodyIdx = rows[0].Count > 1 ? 1 : 0;
            titleIdx = rows[0].Count > 1 ? 0 : -1;
            startRow = 0;
        }

        var docs = new List<FetchedDocument>();
        var skipped = 0;

        for (var i = startRow; i < rows.Count && docs.Count < maxRows; i++)
        {
            var row = rows[i];
            if (bodyIdx >= row.Count || string.IsNullOrWhiteSpace(row[bodyIdx]))
            {
                skipped++;
                continue;
            }

            var body = row[bodyIdx].Trim();
            var title = (titleIdx >= 0 && titleIdx < row.Count && !string.IsNullOrWhiteSpace(row[titleIdx]))
                ? row[titleIdx].Trim()
                : $"CSV Row #{i + 1}";
            var url = (urlIdx >= 0 && urlIdx < row.Count) ? row[urlIdx].Trim() : string.Empty;
            var publishedAt = (dateIdx >= 0 && dateIdx < row.Count && DateTimeOffset.TryParse(row[dateIdx], out var dt))
                ? dt
                : DateTimeOffset.UtcNow;

            docs.Add(new FetchedDocument(
                SourceType.CsvImport,
                $"csv-{i}-{Guid.NewGuid().ToString("N")[..8]}",
                title,
                body,
                url,
                publishedAt));
        }

        return new BulkParseResult(docs, skipped, 0);
    }

    private static List<List<string>> ReadCsvRows(string content)
    {
        var rows = new List<List<string>>();
        var currentRow = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < content.Length && content[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == ',')
                {
                    currentRow.Add(field.ToString());
                    field.Clear();
                }
                else if (c == '\r')
                {
                    // Ignore CR; handle on LF
                }
                else if (c == '\n')
                {
                    currentRow.Add(field.ToString());
                    field.Clear();
                    if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell)))
                    {
                        rows.Add(currentRow);
                    }
                    currentRow = [];
                }
                else
                {
                    field.Append(c);
                }
            }
        }

        if (field.Length > 0 || currentRow.Count > 0)
        {
            currentRow.Add(field.ToString());
            if (currentRow.Any(cell => !string.IsNullOrWhiteSpace(cell)))
            {
                rows.Add(currentRow);
            }
        }

        return rows;
    }

    private static int FindHeaderIndex(List<string> headers, string[] aliases)
    {
        for (var i = 0; i < headers.Count; i++)
        {
            if (aliases.Contains(headers[i], StringComparer.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        return -1;
    }

    private static string? TryGetString(JsonElement root, string[] names)
    {
        foreach (var prop in root.EnumerateObject())
        {
            if (names.Contains(prop.Name, StringComparer.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }
        return null;
    }
}
