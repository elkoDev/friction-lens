using System.Text;
using System.Text.RegularExpressions;

namespace FrictionLens.Api.Extraction;

public sealed record GroundingReport(
    bool Verified,
    string Tier,
    double Score,
    string Reason);

/// <summary>
/// 4-Tier Ordered Grounding Verifier with Negation Parity Check.
/// Prevents LLM hallucinations, cross-paragraph stitching, and polarity flips (dropping "not").
/// </summary>
public static partial class GroundingVerifier
{
    private static readonly HashSet<string> NegationTokens = new(StringComparer.Ordinal)
    {
        "not", "no", "never", "dont", "don't", "doesnt", "doesn't", "didnt", "didn't",
        "cant", "can't", "cannot", "wont", "won't", "without", "lack", "lacks", "lacking", "none"
    };

    public static bool Verify(string rawSourceText, string extractedQuote)
        => Evaluate(rawSourceText, extractedQuote).Verified;

    public static GroundingReport Evaluate(string rawSourceText, string extractedQuote)
    {
        if (string.IsNullOrWhiteSpace(rawSourceText) || string.IsNullOrWhiteSpace(extractedQuote))
        {
            return new GroundingReport(false, "Failed", 0.0, "Empty source or quote");
        }

        var normSource = Normalize(rawSourceText);
        var normQuote = Normalize(extractedQuote);
        var quoteWords = Tokenize(normQuote);

        if (normQuote.Length < 12 || quoteWords.Length < 3)
        {
            return new GroundingReport(false, "Failed", 0.0, "Quote too short to verify (<12 chars or <3 words)");
        }

        // Tier 1: Exact contiguous substring match
        if (normSource.Contains(normQuote, StringComparison.Ordinal))
        {
            return new GroundingReport(true, "Exact", 1.0, "Exact contiguous substring match");
        }

        // Tier 2: Ordered ellipsis multi-quote (e.g., "AB had the bluetooth... AB offered remote programming")
        if (extractedQuote.Contains("...", StringComparison.Ordinal) || extractedQuote.Contains('…'))
        {
            var segments = EllipsisSplitRegex().Split(extractedQuote)
                .Select(Normalize)
                .Where(s => s.Length >= 8)
                .ToArray();

            if (segments.Length >= 2)
            {
                var lastPos = 0;
                var allMatchedInOrder = true;

                foreach (var seg in segments)
                {
                    var pos = normSource.IndexOf(seg, lastPos, StringComparison.Ordinal);
                    if (pos < 0)
                    {
                        // Try ordered window match for this segment starting at lastPos
                        var windowMatch = MatchOrderedWindow(normSource[lastPos..], seg);
                        if (!windowMatch.Matched)
                        {
                            allMatchedInOrder = false;
                            break;
                        }
                        lastPos += windowMatch.EndCharApprox;
                    }
                    else
                    {
                        lastPos = pos + seg.Length;
                    }
                }

                if (allMatchedInOrder)
                {
                    return new GroundingReport(true, "EllipsisOrdered", 0.92, "All ellipsis segments verified in document order");
                }
            }
        }

        // Tier 3: Ordered contiguous window LCS >= 0.85 with Negation Parity Check
        var fuzzy = MatchOrderedWindow(normSource, normQuote);
        if (fuzzy.Matched)
        {
            return new GroundingReport(true, "OrderedWindow", Math.Round(fuzzy.Ratio, 3), "Ordered contiguous window match with negation parity");
        }

        return new GroundingReport(false, "Failed", Math.Round(fuzzy.Ratio, 3), fuzzy.Reason);
    }

    public static bool VerifyShortPhrase(string rawSourceText, string phrase)
    {
        if (string.IsNullOrWhiteSpace(rawSourceText) || string.IsNullOrWhiteSpace(phrase)) return false;
        var normSource = Normalize(rawSourceText);
        var normPhrase = Normalize(phrase);
        if (normPhrase.Length < 3) return false;
        if (normSource.Contains(normPhrase, StringComparison.Ordinal)) return true;

        var pTokens = Tokenize(normPhrase);
        if (pTokens.Length == 0) return false;
        var sTokens = Tokenize(normSource).ToHashSet(StringComparer.Ordinal);
        var overlap = (double)pTokens.Count(t => sTokens.Contains(t)) / pTokens.Length;
        return overlap >= 0.80;
    }

    private static (bool Matched, double Ratio, int EndCharApprox, string Reason) MatchOrderedWindow(string normSource, string normQuote)
    {
        var sTokens = Tokenize(normSource);
        var qTokens = Tokenize(normQuote);
        if (sTokens.Length == 0 || qTokens.Length == 0)
            return (false, 0.0, 0, "No tokens");

        var windowSize = Math.Min(sTokens.Length, (int)Math.Ceiling(qTokens.Length * 1.35) + 1);
        double bestRatio = 0.0;
        var bestStart = 0;

        for (var start = 0; start <= sTokens.Length - Math.Min(qTokens.Length, windowSize); start++)
        {
            var len = Math.Min(windowSize, sTokens.Length - start);
            var lcs = ComputeLcsLength(sTokens.AsSpan(start, len), qTokens);
            var ratio = (double)lcs / qTokens.Length;
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                bestStart = start;
                if (bestRatio >= 0.999) break;
            }
        }

        if (bestRatio < 0.85)
        {
            return (false, bestRatio, 0, $"Ordered window LCS {bestRatio:F2} < 0.85");
        }

        // Negation parity check: ensure quote did not flip polarity by dropping/adding negation words
        var matchedSlice = sTokens.AsSpan(bestStart, Math.Min(windowSize, sTokens.Length - bestStart));
        var sourceNegs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in matchedSlice)
        {
            if (NegationTokens.Contains(t)) sourceNegs.Add(t);
        }

        var quoteNegs = qTokens.Where(t => NegationTokens.Contains(t)).ToHashSet(StringComparer.Ordinal);
        if ((sourceNegs.Count > 0) != (quoteNegs.Count > 0))
        {
            return (false, bestRatio, 0, "Failed negation parity check (potential polarity flip)");
        }

        var approxEnd = Math.Min(normSource.Length, (bestStart + windowSize) * 6);
        return (true, bestRatio, approxEnd, "Matched");
    }

    private static int ComputeLcsLength(ReadOnlySpan<string> a, string[] b)
    {
        var dp = new int[b.Length + 1];
        for (var i = 0; i < a.Length; i++)
        {
            var prev = 0;
            for (var j = 1; j <= b.Length; j++)
            {
                var temp = dp[j];
                if (a[i] == b[j - 1])
                    dp[j] = prev + 1;
                else if (dp[j - 1] > dp[j])
                    dp[j] = dp[j - 1];
                prev = temp;
            }
        }
        return dp[b.Length];
    }

    private static string Normalize(string s)
    {
        var nfkc = s.Normalize(NormalizationForm.FormKC)
            .Replace('\u2019', '\'')
            .Replace('\u2018', '\'')
            .Replace('\u201C', '"')
            .Replace('\u201D', '"')
            .Replace('\u2014', ' ')
            .Replace('\u2013', ' ')
            .ToLowerInvariant();

        var cleaned = NonAlphaNumAposRegex().Replace(nfkc, " ");
        return MultiSpaceRegex().Replace(cleaned, " ").Trim();
    }

    private static string[] Tokenize(string normalized)
        => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);

    [GeneratedRegex(@"(?:\.\.\.|…|\[\.\.\.\])", RegexOptions.Compiled)]
    private static partial Regex EllipsisSplitRegex();

    [GeneratedRegex(@"[^a-z0-9'\s]", RegexOptions.Compiled)]
    private static partial Regex NonAlphaNumAposRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
