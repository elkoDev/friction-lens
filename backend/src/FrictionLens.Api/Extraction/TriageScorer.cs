using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

public sealed record TriageResult(
    bool IsActionable,
    TriageClass TriageClass,
    double Score,
    string Reason);

/// <summary>
/// Stage 2 lightweight deterministic intent triager ($0, <1ms).
/// Separates genuine buying decisions and product friction from neutral how-to questions or news announcements.
/// </summary>
public static class TriageScorer
{
    private static readonly string[] FirstPersonExperience =
    [
        " we ", " our ", " i ", " my ", " us ", "i've", "we've", "we're", "i'm", "ended up", "our team"
    ];

    private static readonly string[] DecisionVerbs =
    [
        "switched", "migrated", "chose", "went with", "picked", "moved off", "dropped",
        "replaced", "evaluated", "choice between", "sticking with", "ditched", "cancelled"
    ];

    private static readonly string[] FrictionSignals =
    [
        "dealbreaker", "deal-breaker", "drawback", "downside", "friction", "painful",
        "bill", "expensive", "lock-in", "lock in", "cold start", "outage", "broken",
        "drive", "missing", "lacks", "slow", "regret", "annoying", "limit", "warranty"
    ];

    private static readonly string[] NeutralQuestionStarts =
    [
        "how do i ", "how to ", "anyone know how ", "is there a tutorial", "can someone help me install",
        "what is the syntax", "just released", "announcing ", "we just launched"
    ];

    public static TriageResult Score(string text, IReadOnlyList<string> matchedEntities, SourceType sourceType)
    {
        var clean = SignalFilter.CleanTextForMatching(text);
        var padded = $" {clean.ToLowerInvariant()} ";
        var trimmedLower = clean.ToLowerInvariant().Trim();

        double score = 0.15;
        var hasDecisionVerb = DecisionVerbs.Any(v => padded.Contains(v, StringComparison.Ordinal));
        var hasFriction = FrictionSignals.Any(f => padded.Contains(f, StringComparison.Ordinal));
        var hasFirstPerson = FirstPersonExperience.Any(p => padded.Contains(p, StringComparison.Ordinal));
        var isMultiEntity = matchedEntities.Count >= 2;
        var isNeutralHowTo = NeutralQuestionStarts.Any(q => trimmedLower.StartsWith(q, StringComparison.Ordinal))
            || (trimmedLower.EndsWith('?') && !hasDecisionVerb && !hasFriction && !hasFirstPerson);

        if (isMultiEntity) score += 0.30;
        if (hasDecisionVerb) score += 0.30;
        if (hasFriction) score += 0.25;
        if (hasFirstPerson) score += 0.15;
        if (isNeutralHowTo) score -= 0.45;

        // First-party manual/CSV imports with explicit friction or multi-entity get a slight recall boost
        var isManual = sourceType is SourceType.ManualPaste or SourceType.CsvImport or SourceType.JsonlImport;
        if (isManual && (hasDecisionVerb || hasFriction || isMultiEntity))
        {
            score += 0.10;
        }

        score = Math.Clamp(score, 0.0, 1.0);

        if (isNeutralHowTo && score < 0.45)
        {
            return new TriageResult(false, TriageClass.NeutralQuestionOrNews, score, "Neutral how-to question or announcement");
        }

        if (score >= 0.45)
        {
            var cls = (isMultiEntity || hasDecisionVerb) ? TriageClass.ActionableDecision : TriageClass.ProductFriction;
            return new TriageResult(true, cls, score, $"Passed Stage 2 triage ({cls})");
        }

        return new TriageResult(false, TriageClass.NoiseOrPromo, score, "Below Stage 2 actionability threshold");
    }
}
