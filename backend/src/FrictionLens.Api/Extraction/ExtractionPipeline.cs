using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Extraction;

public sealed record PipelineRunSummary(
    int TotalReceived,
    int TombstonedSkipped,
    int DeduplicatedSkipped,
    int BlockedByCompliance,
    int PassedFilterCount,
    int PassedTriageCount,
    int InsightsCreatedCount,
    int QuarantinedCount);

public sealed class ExtractionPipeline(
    AppDbContext db,
    IExtractionProvider activeProvider,
    HeuristicExtractionProvider fallbackProvider,
    CanonicalTaxonomyResolver taxonomyResolver,
    ILogger<ExtractionPipeline> logger)
{
    public async Task<PipelineRunSummary> ProcessDocumentsAsync(
        Workspace workspace,
        IReadOnlyList<FetchedDocument> documents,
        CancellationToken ct,
        Guid? ingestionJobId = null)
    {
        if (documents.Count == 0)
        {
            return new PipelineRunSummary(0, 0, 0, 0, 0, 0, 0, 0);
        }

        // Load active tombstones for fast pre-ingestion compliance blocking
        var tombstones = await db.TakedownTombstones
            .AsNoTracking()
            .Where(t => t.WorkspaceId == null || t.WorkspaceId == workspace.Id)
            .ToListAsync(ct);

        var tombstoneUrlHashes = tombstones.Where(t => !string.IsNullOrEmpty(t.NormalizedUrlHash)).Select(t => t.NormalizedUrlHash!).ToHashSet(StringComparer.Ordinal);
        var tombstoneContentHashes = tombstones.Where(t => !string.IsNullOrEmpty(t.ContentHash)).Select(t => t.ContentHash!).ToHashSet(StringComparer.Ordinal);
        var tombstoneExternalIds = tombstones.Where(t => !string.IsNullOrEmpty(t.ExternalId)).Select(t => t.ExternalId!).ToHashSet(StringComparer.Ordinal);

        // Load existing recent hashes & SimHashes for this workspace (batched)
        var existingDocs = await db.RawDocuments
            .AsNoTracking()
            .Where(d => d.WorkspaceId == workspace.Id)
            .Select(d => new { d.ContentHash, d.SimHash64, d.Id })
            .ToListAsync(ct);

        var existingHashes = existingDocs.Select(d => d.ContentHash).ToHashSet(StringComparer.Ordinal);
        var existingSimHashes = existingDocs.Where(d => d.SimHash64 != 0).Select(d => (d.SimHash64, d.Id)).ToList();

        var existingThemes = await db.CanonicalThemes
            .AsNoTracking()
            .Where(t => t.WorkspaceId == workspace.Id && t.MergedIntoThemeId == null)
            .Select(t => t.Slug)
            .ToListAsync(ct);

        var entityAllowTerms = workspace.Entities.SelectMany(e => e.GetAllTerms()).ToList();

        int tombstoned = 0, deduped = 0, blocked = 0, passedFilter = 0, passedTriage = 0, created = 0, quarantined = 0;

        foreach (var doc in documents)
        {
            var urlHash = ComputeNormalizedUrlHash(doc.SourceUrl);
            if ((!string.IsNullOrEmpty(urlHash) && tombstoneUrlHashes.Contains(urlHash))
                || tombstoneExternalIds.Contains(doc.ExternalId))
            {
                tombstoned++;
                continue;
            }

            // Stage 0: Pre-Storage PII Scrubber & GDPR Art. 9 Guardrail
            var titleScrub = PiiScrubber.Scrub(doc.Title, workspace.CompliancePolicy, doc.SourceType, entityAllowTerms);
            var bodyScrub = PiiScrubber.Scrub(doc.Body, workspace.CompliancePolicy, doc.SourceType, entityAllowTerms);

            if (titleScrub.BlockedByPolicy || bodyScrub.BlockedByPolicy)
            {
                blocked++;
                continue;
            }

            var scrubbedTitle = titleScrub.ScrubbedText;
            var scrubbedBody = bodyScrub.ScrubbedText;
            var contentHash = ComputeSha256($"{scrubbedTitle.Trim().ToLowerInvariant()}:{scrubbedBody.Trim().ToLowerInvariant()}");

            if (tombstoneContentHashes.Contains(contentHash))
            {
                tombstoned++;
                continue;
            }

            // Exact deduplication
            if (!existingHashes.Add(contentHash))
            {
                deduped++;
                continue;
            }

            // Near-duplicate 64-bit SimHash check (Hamming distance <= 3 for docs >= 80 chars)
            var simHash = scrubbedBody.Length >= 80 ? ComputeSimHash64(scrubbedBody) : 0L;
            Guid? duplicateOf = null;
            if (simHash != 0)
            {
                var nearMatch = existingSimHashes.FirstOrDefault(x => BitOperations.PopCount((ulong)(x.SimHash64 ^ simHash)) <= 3);
                if (nearMatch.Id != Guid.Empty)
                {
                    duplicateOf = nearMatch.Id;
                }
            }

            // Stage 1: Deterministic Signal Filter v2
            var combinedText = $"{scrubbedTitle} {scrubbedBody}";
            var filterResult = SignalFilter.Evaluate(combinedText, workspace.Entities, doc.SourceType);

            // Stage 2: Lightweight Intent Triage
            TriageResult? triageResult = filterResult.Passed
                ? TriageScorer.Score(combinedText, filterResult.MatchedEntities, doc.SourceType)
                : null;

            var rawDoc = new RawDocument
            {
                WorkspaceId = workspace.Id,
                SourceType = doc.SourceType,
                ExternalId = doc.ExternalId,
                ContentHash = contentHash,
                NormalizedUrlHash = urlHash,
                SimHash64 = simHash,
                DuplicateOfDocumentId = duplicateOf,
                Title = scrubbedTitle,
                Body = scrubbedBody,
                SourceUrl = doc.SourceUrl,
                PublishedAt = doc.PublishedAt,
                PiiRedactionsCount = titleScrub.RedactionsCount + bodyScrub.RedactionsCount,
                ContainsSensitiveTerms = titleScrub.ContainsArt9HealthTerms || bodyScrub.ContainsArt9HealthTerms,
                PassedSignalFilter = filterResult.Passed,
                FilterReason = filterResult.Reason,
                TriageScore = triageResult?.Score ?? 0.0,
                TriageClass = triageResult?.TriageClass
            };

            db.RawDocuments.Add(rawDoc);
            if (simHash != 0) existingSimHashes.Add((simHash, rawDoc.Id));

            if (duplicateOf.HasValue)
            {
                deduped++;
                continue;
            }

            if (!filterResult.Passed) continue;
            passedFilter++;

            if (triageResult is null || !triageResult.IsActionable) continue;
            passedTriage++;

            // Stage 3: Structured Extraction (with 1-shot grounding repair + resilient fallback)
            var extractionCtx = new ExtractionContext(
                workspace.DomainCategory,
                workspace.Entities,
                existingThemes,
                scrubbedTitle,
                scrubbedBody);

            var extracted = await ExecuteWithFallbackAsync(workspace, extractionCtx, ingestionJobId, ct);
            if (extracted is null || !extracted.IsActionable) continue;

            var grounding = GroundingVerifier.Evaluate(combinedText, extracted.VerbatimEvidence);

            // 1-shot repair retry if LLM output failed grounding
            if (!grounding.Verified && activeProvider.ProviderName != fallbackProvider.ProviderName)
            {
                var repairCtx = extractionCtx with
                {
                    RepairInstruction = $"Your previous verbatimEvidence failed grounding ({grounding.Reason}). Copy an exact contiguous substring from the Post Text."
                };
                var repaired = await ExecuteWithFallbackAsync(workspace, repairCtx, ingestionJobId, ct);
                if (repaired is not null && repaired.IsActionable)
                {
                    var repairedGrounding = GroundingVerifier.Evaluate(combinedText, repaired.VerbatimEvidence);
                    if (repairedGrounding.Verified)
                    {
                        extracted = repaired;
                        grounding = repairedGrounding;
                    }
                }
            }

            var isQuarantined = !grounding.Verified;
            if (isQuarantined) quarantined++;

            var calibratedConf = isQuarantined
                ? Math.Min(extracted.Confidence, 0.35)
                : Math.Clamp(0.55 * extracted.Confidence + 0.45 * grounding.Score, 0.1, 0.98);

            var resolvedDrivers = new List<FrictionDriver>();
            foreach (var d in extracted.Drivers)
            {
                var resolved = await taxonomyResolver.ResolveAsync(workspace.Id, d, ct);
                if (!existingThemes.Contains(resolved.Slug)) existingThemes.Add(resolved.Slug);

                resolvedDrivers.Add(new FrictionDriver
                {
                    TargetEntity = d.TargetEntity,
                    Category = d.Category,
                    CanonicalThemeId = resolved.ThemeId,
                    CanonicalTheme = resolved.Slug,
                    DisplayLabel = resolved.DisplayLabel,
                    RawPhrase = d.RawPhrase,
                    Severity = d.Severity,
                    Polarity = d.Polarity
                });
            }

            var insight = new DecisionInsight
            {
                WorkspaceId = workspace.Id,
                RawDocument = rawDoc,
                SourceType = doc.SourceType,
                SourceUrl = doc.SourceUrl,
                SourceTitle = scrubbedTitle,
                ObservedAt = doc.PublishedAt,
                PrimaryEntity = extracted.PrimaryEntity,
                CompetitorsComparedCsv = string.Join(", ", extracted.CompetitorsCompared),
                Outcome = extracted.Outcome,
                Summary = extracted.Summary,
                UnmetRequirement = extracted.UnmetRequirement,
                VerbatimEvidence = extracted.VerbatimEvidence,
                GroundingVerified = grounding.Verified,
                GroundingScore = grounding.Score,
                GroundingTier = grounding.Tier,
                IsQuarantined = isQuarantined,
                Confidence = Math.Round(calibratedConf, 3),
                ExtractorModel = extracted.ModelUsed,
                Drivers = resolvedDrivers
            };

            db.DecisionInsights.Add(insight);
            created++;
        }

        await db.SaveChangesAsync(ct);

        return new PipelineRunSummary(
            documents.Count,
            tombstoned,
            deduped,
            blocked,
            passedFilter,
            passedTriage,
            created,
            quarantined);
    }

    private async Task<ExtractionResult?> ExecuteWithFallbackAsync(
        Workspace workspace,
        ExtractionContext ctx,
        Guid? jobId,
        CancellationToken ct)
    {
        ExtractionResult? result = null;
        var providerUsed = activeProvider.ProviderName;

        try
        {
            result = await activeProvider.ExtractAsync(ctx, ct);
        }
        catch (Exception ex) when (activeProvider.ProviderName != fallbackProvider.ProviderName)
        {
            logger.LogWarning(ex, "Provider '{Provider}' failed; falling back to Heuristic.", activeProvider.ProviderName);
        }

        if (result is null && activeProvider.ProviderName != fallbackProvider.ProviderName)
        {
            providerUsed = fallbackProvider.ProviderName;
            result = await fallbackProvider.ExtractAsync(ctx, ct);
        }

        if (result is not null && (result.PromptTokens > 0 || result.CompletionTokens > 0))
        {
            var estCost = (result.PromptTokens * 0.00000015m) + (result.CompletionTokens * 0.00000060m);
            db.UsageLedger.Add(new UsageLedgerEntry
            {
                WorkspaceId = workspace.Id,
                IngestionJobId = jobId,
                Provider = providerUsed,
                Model = result.ModelUsed,
                Operation = ctx.RepairInstruction is null ? "Extract" : "RepairExtract",
                PromptTokens = result.PromptTokens,
                CompletionTokens = result.CompletionTokens,
                EstimatedCostUsd = estCost
            });
        }

        return result;
    }

    public static string ComputeSha256(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    public static string ComputeNormalizedUrlHash(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;
        var trimmed = url.Trim().ToLowerInvariant();
        var qIdx = trimmed.IndexOf('#');
        if (qIdx >= 0) trimmed = trimmed[..qIdx];
        return ComputeSha256(trimmed.TrimEnd('/'));
    }

    /// <summary>
    /// 64-bit SimHash over normalized 3-word shingles for near-duplicate cross-post detection.
    /// </summary>
    public static long ComputeSimHash64(string text)
    {
        var tokens = text.ToLowerInvariant()
            .Split([' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '"', '\'', '(', ')'], StringSplitOptions.RemoveEmptyEntries);

        if (tokens.Length < 3) return 0;

        var weights = new int[64];
        for (var i = 0; i <= tokens.Length - 3; i++)
        {
            var shingle = $"{tokens[i]} {tokens[i + 1]} {tokens[i + 2]}";
            var hash = BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(shingle)), 0);
            for (var bit = 0; bit < 64; bit++)
            {
                weights[bit] += ((hash >> bit) & 1UL) == 1UL ? 1 : -1;
            }
        }

        ulong result = 0;
        for (var bit = 0; bit < 64; bit++)
        {
            if (weights[bit] > 0) result |= (1UL << bit);
        }
        return unchecked((long)result);
    }
}
