using System.Text.RegularExpressions;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

public sealed record PiiScrubResult(
    string ScrubbedText,
    int RedactionsCount,
    bool ContainsArt9HealthTerms,
    bool BlockedByPolicy,
    string? BlockReason);

/// <summary>
/// Stage 0 pre-storage PII scrubber & GDPR Article 9 sensitive-term guardrail ($0, <1ms).
/// Runs BEFORE RawDocument persistence, deduplication hashing, or LLM submission.
/// </summary>
public static partial class PiiScrubber
{
    private static readonly string[] Art9HealthTerms =
    [
        "diagnosed with", "diagnosis", "medical record", "mrn:", "patient id",
        "cochlear", "audiologist", "insulin pump", "cgm sensor", "prescription",
        "surgery", "hospitalized", "implant surgery", "blood glucose"
    ];

    private static readonly string[] DirectPatientIdMarkers =
    [
        "mrn", "patient id", "dob:", "date of birth", "medical record number", "ssn"
    ];

    public static PiiScrubResult Scrub(
        string text,
        CompliancePolicy policy,
        SourceType sourceType,
        IReadOnlyCollection<string>? allowlistedEntityTerms = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PiiScrubResult(string.Empty, 0, false, false, null);
        }

        var redactions = 0;
        var scrubbed = text;

        scrubbed = SecretRegex().Replace(scrubbed, _ => { redactions++; return "[REDACTED_SECRET]"; });
        scrubbed = EmailRegex().Replace(scrubbed, _ => { redactions++; return "[REDACTED_EMAIL]"; });
        scrubbed = IpV4Regex().Replace(scrubbed, _ => { redactions++; return "[REDACTED_IP]"; });
        scrubbed = PhoneRegex().Replace(scrubbed, m =>
        {
            // Avoid redacting dates (2026-11-24) or version strings (11.0.100)
            var val = m.Value;
            if (DateLikeRegex().IsMatch(val)) return val;
            var digitCount = val.Count(char.IsDigit);
            if (digitCount < 7 || digitCount > 15) return val;
            redactions++;
            return "[REDACTED_PHONE]";
        });

        var allowSet = allowlistedEntityTerms?
            .Select(t => t.Trim().TrimStart('@').ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal) ?? [];

        scrubbed = HandleRegex().Replace(scrubbed, m =>
        {
            var raw = m.Value;
            var bare = raw.StartsWith("u/", StringComparison.OrdinalIgnoreCase) ? raw[2..] : raw.TrimStart('@');
            if (allowSet.Contains(bare.ToLowerInvariant()))
            {
                return raw;
            }
            redactions++;
            return "[REDACTED_HANDLE]";
        });

        var lower = text.ToLowerInvariant();
        var hasArt9 = Art9HealthTerms.Any(t => lower.Contains(t, StringComparison.Ordinal));
        var hasDirectPatientId = DirectPatientIdMarkers.Any(t => lower.Contains(t, StringComparison.Ordinal));
        var isPublicSource = sourceType is not (SourceType.ManualPaste or SourceType.CsvImport or SourceType.JsonlImport);

        if (policy == CompliancePolicy.StrictZeroPii && (redactions > 3 || hasDirectPatientId))
        {
            return new PiiScrubResult(scrubbed, redactions, hasArt9, true, "Blocked by StrictZeroPii policy");
        }

        if (policy == CompliancePolicy.StandardPublic && isPublicSource && hasArt9 && (hasDirectPatientId || redactions > 0))
        {
            return new PiiScrubResult(scrubbed, redactions, hasArt9, true, "Blocked: GDPR Art. 9 health + personal identifier on public source");
        }

        return new PiiScrubResult(scrubbed, redactions, hasArt9, false, null);
    }

    [GeneratedRegex(@"\b(?:sk-[A-Za-z0-9_-]{16,}|ghp_[A-Za-z0-9]{20,}|AKIA[0-9A-Z]{16})\b", RegexOptions.Compiled)]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b", RegexOptions.Compiled)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b", RegexOptions.Compiled)]
    private static partial Regex IpV4Regex();

    [GeneratedRegex(@"(?<!\w)(?:\+?\d{1,3}[\s.-])?(?:\(\d{2,4}\)|\d{2,4})[\s.-]\d{3,4}[\s.-]\d{3,4}(?!\w)", RegexOptions.Compiled)]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"^\d{4}[-/]\d{2}[-/]\d{2}$", RegexOptions.Compiled)]
    private static partial Regex DateLikeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9._-])(?:u/[A-Za-z0-9_-]{3,24}|@[A-Za-z0-9_]{2,30})\b", RegexOptions.Compiled)]
    private static partial Regex HandleRegex();
}
