using System.Text.RegularExpressions;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Extraction;

public sealed record ResolvedTheme(Guid ThemeId, string Slug, string DisplayLabel, string Method);

/// <summary>
/// Phase 2 Canonical Taxonomy Resolver:
/// 1) Built-in & Domain Pack alias seeds -> 2) Exact slug -> 3) Workspace alias dict
/// -> 4) SIMD Cosine Similarity embedding match -> 5) HITL Merge/Rename/Suggestions.
/// </summary>
public sealed partial class CanonicalTaxonomyResolver(
    AppDbContext db,
    IEmbeddingProvider embeddingProvider,
    HashEmbeddingProvider fallbackEmbedder)
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

    public async Task SeedDomainPackThemesAsync(Workspace workspace, DomainPackDto pack, CancellationToken ct)
    {
        foreach (var seed in pack.SeedThemes)
        {
            var embedText = $"{seed.DisplayLabel} ({seed.Category}): {string.Join(", ", seed.Synonyms)}";
            var vec = await EmbedSafeAsync(embedText, ct);

            var theme = new CanonicalThemeRecord
            {
                Workspace = workspace,
                Slug = seed.Slug,
                DisplayLabel = seed.DisplayLabel,
                Category = seed.Category,
                EmbeddingBlob = VectorMath.Pack(vec),
                EmbeddingModel = embeddingProvider.ModelName
            };

            foreach (var syn in seed.Synonyms.Select(NormalizePhrase).Distinct())
            {
                theme.Aliases.Add(new ThemeAlias
                {
                    WorkspaceId = workspace.Id,
                    CanonicalTheme = theme,
                    NormalizedPhrase = syn,
                    Source = $"DomainPack:{pack.PackId}"
                });
            }

            workspace.CanonicalThemes.Add(theme);
        }
    }

    public async Task<ResolvedTheme> ResolveAsync(
        Guid workspaceId,
        ExtractedDriver driver,
        CancellationToken ct)
    {
        var rawSlug = NormalizeSlug(driver.CanonicalTheme);
        var normalizedPhrase = NormalizePhrase(string.IsNullOrWhiteSpace(driver.RawPhrase) ? driver.DisplayLabel : driver.RawPhrase);
        var normalizedLabel = NormalizePhrase(driver.DisplayLabel);

        if (TryMatchBuiltInSeed(normalizedPhrase, normalizedLabel, out var seed))
        {
            rawSlug = seed.Slug;
        }

        var existingThemes = await db.CanonicalThemes
            .Include(t => t.Aliases)
            .Where(t => t.WorkspaceId == workspaceId)
            .ToListAsync(ct);

        // Step 1: Exact slug match (follows MergedIntoThemeId redirect chain)
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

        // Step 3: Embedding Cosine Similarity (SIMD TensorPrimitives)
        var candidateVec = await EmbedSafeAsync($"{driver.DisplayLabel} ({driver.Category}): {driver.RawPhrase}", ct);
        CanonicalThemeRecord? bestEmbedTheme = null;
        float bestCos = 0f;

        foreach (var theme in existingThemes.Where(t => t.MergedIntoThemeId == null))
        {
            var themeVec = VectorMath.Unpack(theme.EmbeddingBlob);
            if (themeVec.Length == 0)
            {
                themeVec = await EmbedSafeAsync($"{theme.DisplayLabel} ({theme.Category}): {string.Join(", ", theme.Aliases.Select(a => a.NormalizedPhrase))}", ct);
                theme.EmbeddingBlob = VectorMath.Pack(themeVec);
                theme.EmbeddingModel = embeddingProvider.ModelName;
            }

            if (themeVec.Length != candidateVec.Length) continue;
            var cos = VectorMath.CosineSimilarity(candidateVec, themeVec);
            if (cos > bestCos)
            {
                bestCos = cos;
                bestEmbedTheme = theme;
            }
        }

        if (bestEmbedTheme is not null && bestCos >= 0.88f)
        {
            EnsureAlias(workspaceId, bestEmbedTheme, normalizedPhrase);
            return new ResolvedTheme(bestEmbedTheme.Id, bestEmbedTheme.Slug, bestEmbedTheme.DisplayLabel, "EmbeddingAuto");
        }

        // Step 4: Mint new CanonicalThemeRecord + store embedding vector
        var label = TryMatchBuiltInSeed(normalizedPhrase, normalizedLabel, out var seedInfo)
            ? seedInfo.Label
            : driver.DisplayLabel;

        var created = new CanonicalThemeRecord
        {
            WorkspaceId = workspaceId,
            Slug = rawSlug,
            DisplayLabel = label,
            Category = driver.Category,
            EmbeddingBlob = VectorMath.Pack(candidateVec),
            EmbeddingModel = embeddingProvider.ModelName
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

    public async Task<IReadOnlyList<MergeSuggestionDto>> FindMergeSuggestionsAsync(Guid workspaceId, CancellationToken ct)
    {
        var active = await db.CanonicalThemes
            .Include(t => t.Aliases)
            .Where(t => t.WorkspaceId == workspaceId && t.MergedIntoThemeId == null)
            .ToListAsync(ct);

        var vectors = new Dictionary<Guid, float[]>();
        foreach (var t in active)
        {
            var v = VectorMath.Unpack(t.EmbeddingBlob);
            if (v.Length == 0)
            {
                v = await EmbedSafeAsync($"{t.DisplayLabel} ({t.Category}): {string.Join(", ", t.Aliases.Select(a => a.NormalizedPhrase))}", ct);
                t.EmbeddingBlob = VectorMath.Pack(v);
                t.EmbeddingModel = embeddingProvider.ModelName;
            }
            vectors[t.Id] = v;
        }
        await db.SaveChangesAsync(ct);

        var suggestions = new List<MergeSuggestionDto>();
        for (var i = 0; i < active.Count; i++)
        {
            for (var j = i + 1; j < active.Count; j++)
            {
                var a = active[i];
                var b = active[j];
                var cos = VectorMath.CosineSimilarity(vectors[a.Id], vectors[b.Id]);
                if (cos >= 0.55f)
                {
                    suggestions.Add(new MergeSuggestionDto(a.Id, a.DisplayLabel, b.Id, b.DisplayLabel, Math.Round(cos, 3)));
                }
            }
        }

        return suggestions.OrderByDescending(s => s.SimilarityScore).Take(10).ToList();
    }

    public async Task<bool> MergeThemesAsync(Guid workspaceId, IReadOnlyList<Guid> sourceThemeIds, Guid targetThemeId, CancellationToken ct)
    {
        var themes = await db.CanonicalThemes
            .Include(t => t.Aliases)
            .Where(t => t.WorkspaceId == workspaceId)
            .ToListAsync(ct);

        var target = themes.FirstOrDefault(t => t.Id == targetThemeId && t.MergedIntoThemeId == null);
        if (target is null) return false;

        var sources = themes.Where(t => sourceThemeIds.Contains(t.Id) && t.Id != targetThemeId && t.MergedIntoThemeId == null).ToList();
        if (sources.Count == 0) return false;

        var sourceIds = sources.Select(s => s.Id).ToHashSet();
        var sourceSlugs = sources.Select(s => s.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var driversToMove = await db.FrictionDrivers
            .Include(d => d.DecisionInsight)
            .Where(d => d.DecisionInsight != null && d.DecisionInsight.WorkspaceId == workspaceId
                && ((d.CanonicalThemeId.HasValue && sourceIds.Contains(d.CanonicalThemeId.Value)) || sourceSlugs.Contains(d.CanonicalTheme)))
            .ToListAsync(ct);

        foreach (var d in driversToMove)
        {
            d.CanonicalThemeId = target.Id;
            d.CanonicalTheme = target.Slug;
            d.DisplayLabel = target.DisplayLabel;
        }

        var existingTargetAliases = target.Aliases.Select(a => a.NormalizedPhrase).ToHashSet(StringComparer.Ordinal);
        foreach (var src in sources)
        {
            src.MergedIntoThemeId = target.Id;
            var srcLabelNorm = NormalizePhrase(src.DisplayLabel);
            if (!string.IsNullOrWhiteSpace(srcLabelNorm) && existingTargetAliases.Add(srcLabelNorm))
            {
                db.ThemeAliases.Add(new ThemeAlias
                {
                    WorkspaceId = workspaceId,
                    CanonicalThemeId = target.Id,
                    NormalizedPhrase = srcLabelNorm,
                    Source = "Merge"
                });
            }

            foreach (var alias in src.Aliases.ToList())
            {
                if (existingTargetAliases.Add(alias.NormalizedPhrase))
                {
                    alias.CanonicalThemeId = target.Id;
                }
                else
                {
                    db.ThemeAliases.Remove(alias);
                }
            }
        }

        db.ThemeMergeAuditLogs.Add(new ThemeMergeAuditLog
        {
            WorkspaceId = workspaceId,
            Action = "Merge",
            Details = $"Merged [{string.Join(", ", sources.Select(s => s.Slug))}] into '{target.Slug}' ({driversToMove.Count} drivers updated)."
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RenameThemeAsync(Guid workspaceId, Guid themeId, string newLabel, DriverCategory? newCategory, CancellationToken ct)
    {
        var theme = await db.CanonicalThemes.FirstOrDefaultAsync(t => t.Id == themeId && t.WorkspaceId == workspaceId, ct);
        if (theme is null || string.IsNullOrWhiteSpace(newLabel)) return false;

        var oldLabel = theme.DisplayLabel;
        theme.DisplayLabel = newLabel.Trim();
        if (newCategory.HasValue) theme.Category = newCategory.Value;

        var drivers = await db.FrictionDrivers
            .Where(d => d.CanonicalThemeId == theme.Id || d.CanonicalTheme == theme.Slug)
            .ToListAsync(ct);

        foreach (var d in drivers)
        {
            d.DisplayLabel = theme.DisplayLabel;
            if (newCategory.HasValue) d.Category = newCategory.Value;
        }

        db.ThemeMergeAuditLogs.Add(new ThemeMergeAuditLog
        {
            WorkspaceId = workspaceId,
            Action = "Rename",
            Details = $"Renamed '{oldLabel}' -> '{theme.DisplayLabel}'."
        });

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<float[]> EmbedSafeAsync(string text, CancellationToken ct)
    {
        try
        {
            return await embeddingProvider.EmbedAsync(text, ct);
        }
        catch
        {
            return await fallbackEmbedder.EmbedAsync(text, ct);
        }
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

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex NonSlugRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex MultiSpaceRegex();
}
