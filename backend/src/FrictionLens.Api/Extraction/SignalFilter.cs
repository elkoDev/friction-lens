using System.Text.RegularExpressions;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

public sealed record SignalFilterResult(
    bool Passed,
    string Reason,
    int WeightedScore,
    IReadOnlyList<string> MatchedEntities);

/// <summary>
/// Stage 1 deterministic signal-to-noise filter v2 ($0 cost).
/// Strips code blocks/URLs/quoted replies, guards short aliases, and scores weighted decision/friction markers.
/// </summary>
public static partial class SignalFilter
{
    private static readonly string[] SwitchDecisionMarkers =
    [
        "switched from", "migrated from", "moved from", "moved away from", "ripped out",
        "ditched", "choice between", "chose ", "went with", "picked ", "instead of",
        "alternative to", "compared to", " vs ", " vs.", " versus ", "over "
    ];

    private static readonly string[] FrictionSeverityMarkers =
    [
        "dealbreaker", "deal-breaker", "drawback", "downside", "friction", "regret",
        "unusable", "painful", "showstopper", "lacks ", "missing ", "wish it had",
        "problem with", "issue with", "annoying", "broken", "workaround", "frustrating"
    ];

    private static readonly string[] CommercialAccessMarkers =
    [
        "too expensive", "surprise bill", "pricing", "pricing cliff", "vendor lock-in",
        "lock-in", "lock in", "paywall", "cold start", "hours drive", "h drive",
        "hr drive", "clinic distance", "remote programming", "tele-audiology",
        "self-host", "self host", "warranty", "replacement", "outage"
    ];

    private static readonly string[] NegativeChatterExclusions =
    [
        "who is hiring", "we are hiring", "job posting", "apply here",
        "release notes", "full changelog", "stock price", "quarterly earnings"
    ];

    public static SignalFilterResult Evaluate(string text, IReadOnlyList<TrackedEntity> entities, SourceType sourceType)
    {
        var clean = CleanTextForMatching(text);
        var wordCount = clean.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var isManual = sourceType is SourceType.ManualPaste or SourceType.CsvImport or SourceType.JsonlImport;

        if (string.IsNullOrWhiteSpace(clean) || clean.Length < (isManual ? 25 : 35) || wordCount < (isManual ? 5 : 8))
        {
            return new SignalFilterResult(false, "Text too short after stripping code/URLs", 0, []);
        }

        var lower = clean.ToLowerInvariant();
        var matchedEntities = new List<string>();

        foreach (var entity in entities)
        {
            foreach (var term in entity.GetAllTerms())
            {
                if (MatchesEntityTerm(clean, lower, term))
                {
                    matchedEntities.Add(entity.Name);
                    break;
                }
            }
        }

        if (matchedEntities.Count == 0)
        {
            return new SignalFilterResult(false, "No tracked entities mentioned", 0, []);
        }

        // Guard against 2-char alias false positives when only 1 entity matched (e.g. "AB" in casual text)
        if (matchedEntities.Count == 1 && !isManual)
        {
            var matchedEntity = entities.First(e => e.Name == matchedEntities[0]);
            var matchedOnlyViaShortAlias = !ContainsWholeTerm(lower, matchedEntity.Name.ToLowerInvariant())
                && matchedEntity.GetAllTerms().Where(t => t.Length <= 2).Any(t => ContainsExactCaseWholeTerm(clean, t));

            if (matchedOnlyViaShortAlias && !CommercialAccessMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal)))
            {
                return new SignalFilterResult(false, "Short alias without corroborating context", 0, []);
            }
        }

        var score = 0;
        if (SwitchDecisionMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))) score += 3;
        if (FrictionSeverityMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))) score += 2;
        if (CommercialAccessMarkers.Any(m => lower.Contains(m, StringComparison.Ordinal))) score += 2;
        if (NegativeChatterExclusions.Any(m => lower.Contains(m, StringComparison.Ordinal))) score -= 5;

        if (isManual)
        {
            return new SignalFilterResult(true, "First-party/manual import with entity match", Math.Max(score, 2), matchedEntities);
        }

        if (score < 0 && matchedEntities.Count < 2)
        {
            return new SignalFilterResult(false, "Excluded promotional/hiring/release chatter", score, matchedEntities);
        }

        if (matchedEntities.Count >= 2 && score >= 0)
        {
            return new SignalFilterResult(true, "Multi-entity comparison detected", score + 2, matchedEntities);
        }

        if (score >= 2)
        {
            return new SignalFilterResult(true, "Entity + weighted friction/decision signal detected", score, matchedEntities);
        }

        return new SignalFilterResult(false, "Single entity mention without strong decision/friction signal", score, matchedEntities);
    }

    public static string CleanTextForMatching(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        var s = FencedCodeRegex().Replace(input, " ");
        s = QuotedLineRegex().Replace(s, " ");
        s = UrlRegex().Replace(s, " ");
        return MultiSpaceRegex().Replace(s, " ").Trim();
    }

    private static bool MatchesEntityTerm(string original, string lower, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        // Short aliases (<=2 chars like "AB", "Go") require exact uppercase/case match to avoid matching "about" or "a/b"
        if (term.Length <= 2)
        {
            return ContainsExactCaseWholeTerm(original, term);
        }
        return ContainsWholeTerm(lower, term.ToLowerInvariant());
    }

    private static bool ContainsWholeTerm(string haystackLower, string needleLower)
    {
        var idx = haystackLower.IndexOf(needleLower, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var leftOk = idx == 0 || !char.IsLetterOrDigit(haystackLower[idx - 1]);
            var end = idx + needleLower.Length;
            var rightOk = end >= haystackLower.Length || !char.IsLetterOrDigit(haystackLower[end]);
            if (leftOk && rightOk) return true;
            idx = haystackLower.IndexOf(needleLower, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private static bool ContainsExactCaseWholeTerm(string haystack, string needle)
    {
        var idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            var leftOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            var end = idx + needle.Length;
            var rightOk = end >= haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            // Avoid matching "A/B" as "AB"
            if (leftOk && rightOk) return true;
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    [GeneratedRegex(@"```[\s\S]*?```", RegexOptions.Compiled)]
    private static partial Regex FencedCodeRegex();

    [GeneratedRegex(@"(?m)^>.*$", RegexOptions.Compiled)]
    private static partial Regex QuotedLineRegex();

    [GeneratedRegex(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
