using System.Text.RegularExpressions;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Extraction;

public sealed record ResolvedTheme(Guid ThemeId, string Slug, string DisplayLabel, string Method);

/// <summary>
/// Phase 1 Canonical Taxonomy Resolver: collapses synonym phrases
/// (e.g., "tele-audiology" == "remote fitting" == "remote programming")
/// using workspace alias dictionaries + normalized stem/token Jaccard matching.
/// </summary>
public sealed partial class CanonicalTaxonomyResolver(AppDbContext db)
{
    private static readonly Dictionary<string, (string Slug, string Label, DriverCategory Category)> BuiltInSynonymSeeds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tele-audiology"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),
        ["tele audiology"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),
        ["remote fitting"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),
        ["online mapping"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),
        ["remote tuning"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),
        ["remote programming"] = ("remote_programming", "Remote Programming Support", DriverCategory.ServiceSupport),

        ["bill shock"] = ("pricing_predictability", "Pricing & TCO Predictability", DriverCategory.PricingTco),
        ["billing shock"] = ("pricing_predictability", "Pricing & TCO Predictability", DriverCategory.PricingTco),
        ["surprise bill"] = ("pricing_predictability", "Pricing & TCO Predictability", DriverCategory.PricingTco),
        ["unpredictable usage pricing"] = ("pricing_predictability", "Pricing & TCO Predictability", DriverCategory.PricingTco),
        ["pricing cliff"] = ("pricing_predictability", "Pricing & TCO Predictability", DriverCategory.PricingTco),

        ["cold starts"] = ("reliability_and_latency", "Reliability & Cold-Start Latency", DriverCategory.ReliabilityQuality),
        ["wake from idle"] = ("reliability_and_latency", "Reliability & Cold-Start Latency", DriverCategory.ReliabilityQuality),
        ["spin-up time"] = ("reliability_and_latency", "Reliability & Cold-Start Latency", DriverCategory.ReliabilityQuality),

        ["proprietary lock-in"] = ("vendor_lock_in", "Vendor Lock-In & Portability", DriverCategory.EcosystemLockin),
        ["vendor lock"] = ("vendor_lock_in", "Vendor Lock-In & Portability", DriverCategory.EcosystemLockin),
        ["hard to migrate"] = ("vendor_lock_in", "Vendor Lock-In & Portability", DriverCategory.EcosystemLockin),

        ["long drive"] = ("clinic_travel_distance", "Clinic Travel Distance", DriverCategory.LogisticsAccess),
        ["7h drive"] = ("clinic_travel_distance", "Clinic Travel Distance", DriverCategory.LogisticsAccess),
        ["clinic commute"] = ("clinic_travel_distance", "Clinic Travel Distance", DriverCategory.LogisticsAccess),
    };

    public async Task<ResolvedTheme> ResolveAsync(
        Guid workspaceId,
        ExtractedDriver driver,
        CancellationToken ct)
    {
        var rawSlug = NormalizeSlug(driver.CanonicalTheme);
        var normalizedPhrase = NormalizePhrase(string.IsNullOrWhiteSpace(driver.RawPhrase) ? driver.DisplayLabel : driver.RawPhrase);
        var normalizedLabel = NormalizePhrase(driver.DisplayLabel);

        // Check built-in domain-agnostic synonym seeds first
        if (TryMatchBuiltInSeed(normalizedPhrase, normalizedLabel, out var seed))
        {
            rawSlug = seed.Slug;
        }

        var existingThemes = await db.CanonicalThemes
            .Include(t => t.Aliases)
            .Where(t => t.WorkspaceId == workspaceId)
            .ToListAsync(ct);

        // Step 1: Exact slug match (follow merge pointer if merged)
        var exact = existingThemes.FirstOrDefault(t => t.Slug == rawSlug);
        if (exact is not null)
        {
            var target = FollowMerge(exact, existingThemes);
            EnsureAlias(workspaceId, target, normalizedPhrase);
            return new ResolvedTheme(target.Id, target.Slug, target.DisplayLabel, "ExactSlug");
        }

        // Step 2: Workspace Alias Dictionary lookup
        foreach (var theme in existingThemes)
        {
            if (theme.Aliases.Any(a => a.NormalizedPhrase == normalizedPhrase || a.NormalizedPhrase == normalizedLabel))
            {
                var target = FollowMerge(theme, existingThemes);
                return new ResolvedTheme(target.Id, target.Slug, target.DisplayLabel, "AliasDict");
            }
        }

        // Step 3: Normalized Stem Token Jaccard within same category
        var candidateTokens = StemTokens($"{driver.DisplayLabel} {driver.RawPhrase}");
        CanonicalThemeRecord? bestTheme = null;
        double bestScore = 0.0;

        foreach (var theme in existingThemes.Where(t => t.MergedIntoThemeId == null && t.Category == driver.Category))
        {
            var themeText = $"{theme.DisplayLabel} {string.Join(' ', theme.Aliases.Select(a => a.NormalizedPhrase))}";
            var themeTokens = StemTokens(themeText);
            var jaccard = TokenJaccard(candidateTokens, themeTokens);
            if (jaccard > bestScore)
            {
                bestScore = jaccard;
                bestTheme = theme;
            }
        }

        if (bestTheme is not null && bestScore >= 0.60)
        {
            EnsureAlias(workspaceId, bestTheme, normalizedPhrase);
            return new ResolvedTheme(bestTheme.Id, bestTheme.Slug, bestTheme.DisplayLabel, "StemOverlap");
        }

        // Step 4: Mint new CanonicalThemeRecord + alias
        var label = TryMatchBuiltInSeed(normalizedPhrase, normalizedLabel, out var seedInfo)
            ? seedInfo.Label
            : driver.DisplayLabel;

        var created = new CanonicalThemeRecord
        {
            WorkspaceId = workspaceId,
            Slug = rawSlug,
            DisplayLabel = label,
            Category = driver.Category
        };

        if (!string.IsNullOrWhiteSpace(normalizedPhrase))
        {
            created.Aliases.Add(new ThemeAlias
            {
                WorkspaceId = workspaceId,
                CanonicalTheme = created,
                NormalizedPhrase = normalizedPhrase,
                Source = "AutoMint"
            });
        }

        db.CanonicalThemes.Add(created);
        return new ResolvedTheme(created.Id, created.Slug, created.DisplayLabel, "NewTheme");
    }

    public static string ResolveCanonicalSlugOffline(string rawSlug, string displayLabel, string rawPhrase)
    {
        var normPhrase = NormalizePhrase(rawPhrase);
        var normLabel = NormalizePhrase(displayLabel);
        if (TryMatchBuiltInSeed(normPhrase, normLabel, out var seed))
        {
            return seed.Slug;
        }
        return NormalizeSlug(rawSlug);
    }

    private static bool TryMatchBuiltInSeed(string normPhrase, string normLabel, out (string Slug, string Label, DriverCategory Category) match)
    {
        foreach (var kvp in BuiltInSynonymSeeds)
        {
            if (normPhrase.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)
                || normLabel.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase))
            {
                match = kvp.Value;
                return true;
            }
        }
        match = default;
        return false;
    }

    private static CanonicalThemeRecord FollowMerge(CanonicalThemeRecord theme, List<CanonicalThemeRecord> all)
    {
        var current = theme;
        var hops = 0;
        while (current.MergedIntoThemeId.HasValue && hops++ < 5)
        {
            var next = all.FirstOrDefault(t => t.Id == current.MergedIntoThemeId.Value);
            if (next is null) break;
            current = next;
        }
        return current;
    }

    private void EnsureAlias(Guid workspaceId, CanonicalThemeRecord theme, string normalizedPhrase)
    {
        if (string.IsNullOrWhiteSpace(normalizedPhrase)) return;
        if (theme.Aliases.Any(a => a.NormalizedPhrase == normalizedPhrase)) return;

        var alias = new ThemeAlias
        {
            WorkspaceId = workspaceId,
            CanonicalThemeId = theme.Id,
            NormalizedPhrase = normalizedPhrase,
            Source = "AutoResolved"
        };
        theme.Aliases.Add(alias);
        db.ThemeAliases.Add(alias);
    }

    public static string NormalizeSlug(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "general_friction";
        var slug = NonSlugRegex().Replace(input.Trim().ToLowerInvariant(), "_").Trim('_');
        return string.IsNullOrWhiteSpace(slug) ? "general_friction" : slug;
    }

    public static string NormalizePhrase(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return MultiSpaceRegex().Replace(input.Trim().ToLowerInvariant(), " ");
    }

    private static HashSet<string> StemTokens(string text)
    {
        var words = NonWordRegex().Replace(text.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in words)
        {
            if (w.Length <= 2) continue;
            var stem = w;
            if (stem.EndsWith("ing", StringComparison.Ordinal) && stem.Length > 5) stem = stem[..^3];
            else if (stem.EndsWith("ed", StringComparison.Ordinal) && stem.Length > 4) stem = stem[..^2];
            else if (stem.EndsWith("s", StringComparison.Ordinal) && stem.Length > 4) stem = stem[..^1];
            set.Add(stem);
        }
        return set;
    }

    private static double TokenJaccard(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0.0;
        var intersection = a.Count(b.Contains);
        var union = a.Count + b.Count - intersection;
        return union == 0 ? 0.0 : (double)intersection / union;
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex(@"[^a-z0-9\s]", RegexOptions.Compiled)]
    private static partial Regex NonWordRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
