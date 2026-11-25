using System.Text.RegularExpressions;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

/// <summary>
/// Zero-cost, offline deterministic extraction provider (v2).
/// Supports multi-domain switching disambiguation, canonical synonym mapping, and exact verbatim extraction.
/// </summary>
public sealed partial class HeuristicExtractionProvider : IExtractionProvider
{
    public string ProviderName => "Heuristic";
    public string ModelName => "deterministic-rules-v2";

    private static readonly (string Pattern, DriverCategory Category, string Theme, string Label, DriverPolarity DefaultPolarity, DriverSeverity DefaultSeverity)[] DriverRules =
    [
        ("remote programming|tele-audiology|tele audiology|remote fitting|remote tuning|online mapping", DriverCategory.ServiceSupport, "remote_programming", "Remote Programming Support", DriverPolarity.Advantage, DriverSeverity.DealBreaker),
        ("bluetooth|streaming|wireless|direct audio", DriverCategory.FeatureGap, "bluetooth_connectivity", "Native Bluetooth Connectivity", DriverPolarity.Advantage, DriverSeverity.MajorFriction),
        (@"(\d+\s*(h|hr|hour|hours)\s*drive|clinic distance|long drive|travel\s+distance|far away|clinic commute)", DriverCategory.LogisticsAccess, "clinic_travel_distance", "Clinic Travel Distance", DriverPolarity.Drawback, DriverSeverity.DealBreaker),
        ("pricing|expensive|bill shock|billing shock|surprise bill|unpredictable.*pricing|paywall|enterprise tier|seat price|pricing cliff|sso tax|overpriced", DriverCategory.PricingTco, "pricing_predictability", "Pricing & TCO Predictability", DriverPolarity.Drawback, DriverSeverity.DealBreaker),
        ("lock-in|lock in|proprietary|vendor lock|hard to migrate|export|portability", DriverCategory.EcosystemLockin, "vendor_lock_in", "Vendor Lock-In & Portability", DriverPolarity.Drawback, DriverSeverity.DealBreaker),
        ("self-host|self host|docker|on-prem|local dev|local development", DriverCategory.DeveloperExperience, "self_hosting_and_local_dev", "Self-Hosting & Local Dev Experience", DriverPolarity.Advantage, DriverSeverity.MajorFriction),
        ("cold start|wake from idle|spin-up time|latency|slow|timeout|downtime|outage|unreliable|crash", DriverCategory.ReliabilityQuality, "reliability_and_latency", "Reliability & Cold-Start Latency", DriverPolarity.Drawback, DriverSeverity.DealBreaker),
        ("postgres|sql|relational|joins|branching|schema", DriverCategory.FeatureGap, "sql_and_branching", "Native SQL & Database Branching", DriverPolarity.Advantage, DriverSeverity.MajorFriction),
        ("docs|documentation|dx|sdk|typescript|types|type safety|onboarding|setup", DriverCategory.DeveloperExperience, "dx_and_documentation", "Developer Experience & Docs", DriverPolarity.Advantage, DriverSeverity.MinorAnnoyance),
        ("support|customer service|response time|ticket|warranty|replacement", DriverCategory.ServiceSupport, "customer_support_speed", "Customer Support & Warranty", DriverPolarity.Drawback, DriverSeverity.MajorFriction),
        ("battery|battery life|charging|rechargeable", DriverCategory.ReliabilityQuality, "battery_life", "Battery Life & Charging", DriverPolarity.Drawback, DriverSeverity.MajorFriction),
        ("reporting|dashboard|analytics|salesforce sync|crm integration|permissions", DriverCategory.FeatureGap, "integrations_and_reporting", "Integrations & Reporting", DriverPolarity.Drawback, DriverSeverity.MajorFriction)
    ];

    public Task<ExtractionResult?> ExtractAsync(ExtractionContext context, CancellationToken ct)
    {
        var fullText = string.IsNullOrWhiteSpace(context.Title) ? context.Body : $"{context.Title}. {context.Body}";
        var lower = fullText.ToLowerInvariant();

        var mentioned = context.Entities
            .Where(e => e.GetAllTerms().Any(t => MatchesTerm(fullText, lower, t)))
            .ToList();

        if (mentioned.Count == 0)
        {
            return Task.FromResult<ExtractionResult?>(null);
        }

        var (primary, outcome) = ResolvePrimaryAndOutcome(fullText, lower, mentioned);
        var competitors = mentioned.Where(e => e.Name != primary.Name).Select(e => e.Name).ToList();
        var drivers = ExtractDrivers(fullText, primary, mentioned);

        if (drivers.Count == 0)
        {
            drivers.Add(new ExtractedDriver(
                primary.Name,
                DriverCategory.Other,
                "general_product_comparison",
                "General Product Evaluation",
                "product comparison",
                DriverSeverity.MinorAnnoyance,
                outcome is DecisionOutcome.SwitchedAway or DecisionOutcome.RejectedPrimary
                    ? DriverPolarity.Drawback
                    : DriverPolarity.Advantage));
        }

        var verbatim = PickBestVerbatimSpan(context.Body);
        var unmet = InferUnmetRequirement(drivers, competitors.FirstOrDefault() ?? primary.Name);
        var summary = competitors.Count > 0
            ? $"User compared {primary.Name} against {string.Join(", ", competitors)} ({outcome}); key drivers: {string.Join(", ", drivers.Select(d => d.DisplayLabel).Distinct().Take(3))}."
            : $"User discussed {primary.Name} highlighting {string.Join(", ", drivers.Select(d => d.DisplayLabel).Distinct().Take(2))}.";

        var result = new ExtractionResult(
            IsActionable: true,
            PrimaryEntity: primary.Name,
            CompetitorsCompared: competitors,
            Outcome: outcome,
            Summary: summary,
            UnmetRequirement: unmet,
            VerbatimEvidence: verbatim,
            Confidence: drivers.Any(d => d.Severity == DriverSeverity.DealBreaker) ? 0.80 : 0.72,
            ModelUsed: ModelName,
            Drivers: drivers);

        return Task.FromResult<ExtractionResult?>(result);
    }

    private static (TrackedEntity Primary, DecisionOutcome Outcome) ResolvePrimaryAndOutcome(
        string original,
        string lower,
        List<TrackedEntity> mentioned)
    {
        // 1. Explicit winner verbs: "switched to X", "migrated to X", "went with X", "chose X", "picked X"
        foreach (var entity in mentioned)
        {
            foreach (var term in entity.GetAllTerms().Select(t => t.ToLowerInvariant()))
            {
                if (lower.Contains($"switched to {term}", StringComparison.Ordinal)
                    || lower.Contains($"migrated to {term}", StringComparison.Ordinal)
                    || lower.Contains($"moved to {term}", StringComparison.Ordinal)
                    || lower.Contains($"went with {term}", StringComparison.Ordinal)
                    || lower.Contains($"chose {term}", StringComparison.Ordinal)
                    || lower.Contains($"choosing {term}", StringComparison.Ordinal)
                    || lower.Contains($"picked {term}", StringComparison.Ordinal)
                    || lower.Contains($"sticking with {term}", StringComparison.Ordinal))
                {
                    return (entity, DecisionOutcome.ChosePrimary);
                }
            }
        }

        // 2. Explicit positive attribution in a choice: "AB offered...", "AB had..."
        foreach (var entity in mentioned)
        {
            foreach (var term in entity.GetAllTerms())
            {
                var pattern = $@"\b{Regex.Escape(term)}\s+(?:offered|had|gave|supports|includes|won)\b";
                if (Regex.IsMatch(original, pattern, term.Length <= 2 ? RegexOptions.None : RegexOptions.IgnoreCase))
                {
                    return (entity, DecisionOutcome.ChosePrimary);
                }
            }
        }

        // 3. Explicit rejection / switch-away: "ditched X", "dropped X", "switched from X", "migrated off X", "regret X"
        foreach (var entity in mentioned)
        {
            foreach (var term in entity.GetAllTerms().Select(t => t.ToLowerInvariant()))
            {
                if (lower.Contains($"switched from {term}", StringComparison.Ordinal)
                    || lower.Contains($"migrated from {term}", StringComparison.Ordinal)
                    || lower.Contains($"migrated off {term}", StringComparison.Ordinal)
                    || lower.Contains($"moved off {term}", StringComparison.Ordinal)
                    || lower.Contains($"moved away from {term}", StringComparison.Ordinal)
                    || lower.Contains($"ditched {term}", StringComparison.Ordinal)
                    || lower.Contains($"dropped {term}", StringComparison.Ordinal)
                    || lower.Contains($"leaving {term}", StringComparison.Ordinal))
                {
                    return (entity, DecisionOutcome.SwitchedAway);
                }

                if (lower.Contains($"dealbreaker with {term}", StringComparison.Ordinal)
                    || lower.Contains($"deal-breaker with {term}", StringComparison.Ordinal)
                    || lower.Contains($"regret {term}", StringComparison.Ordinal)
                    || lower.Contains($"didn't go with {term}", StringComparison.Ordinal))
                {
                    return (entity, DecisionOutcome.RejectedPrimary);
                }
            }
        }

        if (lower.Contains("workaround", StringComparison.Ordinal) || lower.Contains("hack around", StringComparison.Ordinal))
        {
            return (mentioned[0], DecisionOutcome.Workaround);
        }

        if (lower.Contains("dealbreaker", StringComparison.Ordinal) || lower.Contains("deal-breaker", StringComparison.Ordinal))
        {
            return (mentioned[0], DecisionOutcome.RejectedPrimary);
        }

        return (mentioned[0], DecisionOutcome.Evaluating);
    }

    private static List<ExtractedDriver> ExtractDrivers(
        string fullText,
        TrackedEntity primary,
        List<TrackedEntity> mentioned)
    {
        var list = new List<ExtractedDriver>();
        var secondary = mentioned.FirstOrDefault(e => e.Name != primary.Name) ?? primary;

        foreach (var rule in DriverRules)
        {
            var match = Regex.Match(fullText, rule.Pattern, RegexOptions.IgnoreCase);
            if (!match.Success) continue;

            var windowStart = Math.Max(0, match.Index - 70);
            var windowLen = Math.Min(fullText.Length - windowStart, 140);
            var window = fullText.Substring(windowStart, windowLen);
            var windowLower = window.ToLowerInvariant();

            // Check if the surrounding window explicitly names another entity
            var target = primary.Name;
            foreach (var ent in mentioned)
            {
                if (ent.GetAllTerms().Any(t => MatchesTerm(window, windowLower, t)))
                {
                    target = ent.Name;
                    break;
                }
            }

            // If polarity is Drawback and primary wasn't explicitly named in window, assign to rival when comparing
            if (rule.DefaultPolarity == DriverPolarity.Drawback && target == primary.Name && secondary.Name != primary.Name
                && !primary.GetAllTerms().Any(t => MatchesTerm(window, windowLower, t)))
            {
                target = secondary.Name;
            }

            // Flip Advantage -> Drawback if negated locally (e.g., "no remote programming", "lacks bluetooth")
            var polarity = rule.DefaultPolarity;
            if (polarity == DriverPolarity.Advantage && LocalNegationRegex().IsMatch(windowLower))
            {
                polarity = DriverPolarity.Drawback;
            }

            list.Add(new ExtractedDriver(
                target,
                rule.Category,
                rule.Theme,
                rule.Label,
                match.Value,
                rule.DefaultSeverity,
                polarity));
        }

        return list;
    }

    private static bool MatchesTerm(string original, string lower, string term)
    {
        if (string.IsNullOrWhiteSpace(term)) return false;
        if (term.Length <= 2)
        {
            return Regex.IsMatch(original, $@"(?<![A-Za-z0-9]){Regex.Escape(term)}(?![A-Za-z0-9])");
        }
        return Regex.IsMatch(lower, $@"(?<![a-z0-9]){Regex.Escape(term.ToLowerInvariant())}(?![a-z0-9])");
    }

    private static string PickBestVerbatimSpan(string body)
    {
        var trimmed = body.Trim();
        if (trimmed.Length <= 280) return trimmed;
        var sentences = SentenceSplitRegex().Split(trimmed);
        var best = sentences.OrderByDescending(s => s.Length is >= 40 and <= 260 ? 2 : 1).FirstOrDefault() ?? trimmed[..260];
        return best.Length > 280 ? best[..280].Trim() : best.Trim();
    }

    private static string InferUnmetRequirement(List<ExtractedDriver> drivers, string targetEntity)
    {
        var topDrawback = drivers.FirstOrDefault(d => d.Polarity == DriverPolarity.Drawback);
        if (topDrawback is not null)
        {
            return $"Eliminate {topDrawback.DisplayLabel.ToLowerInvariant()} friction for {topDrawback.TargetEntity} users.";
        }

        var topAdvantage = drivers.FirstOrDefault();
        return topAdvantage is not null
            ? $"Match or exceed competitor capability on {topAdvantage.DisplayLabel.ToLowerInvariant()} (vs. {targetEntity})."
            : "Clarify product positioning against competing alternatives.";
    }

    [GeneratedRegex(@"(?<=[\.!\?])\s+", RegexOptions.Compiled)]
    private static partial Regex SentenceSplitRegex();

    [GeneratedRegex(@"\b(?:no|not|without|lacks|missing|never|didn't|doesn't|don't)\b", RegexOptions.Compiled)]
    private static partial Regex LocalNegationRegex();
}
