using System.Text.RegularExpressions;
using FrictionLens.Api.Analytics;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using FrictionLens.Api.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Endpoints;

public static partial class ApiEndpoints
{
    public static void MapFrictionLensApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // System health & active provider configuration
        api.MapGet("/status", (IConfiguration cfg, IExtractionProvider provider) =>
        {
            var dbProvider = cfg["Database:Provider"] ?? "Sqlite";
            var ollamaUrl = cfg["Extraction:OllamaBaseUrl"];
            var cloudKey = cfg["Extraction:ApiKey"] ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
            var ytKey = cfg["Connectors:YouTubeApiKey"] ?? Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");

            return Results.Ok(new SystemStatusDto(
                dbProvider,
                provider.ProviderName,
                provider.ModelName,
                !string.IsNullOrWhiteSpace(ollamaUrl),
                !string.IsNullOrWhiteSpace(cloudKey),
                !string.IsNullOrWhiteSpace(ytKey),
                "0.2.0-phase1"));
        });

        // List all workspaces (projected counts, no eager-loading of all insights)
        api.MapGet("/workspaces", async (AppDbContext db, CancellationToken ct) =>
        {
            var workspaces = await db.Workspaces
                .AsNoTracking()
                .Include(w => w.Entities)
                .Include(w => w.Sources)
                    .ThenInclude(s => s.SyncState)
                .ToListAsync(ct);

            var counts = await db.DecisionInsights
                .AsNoTracking()
                .Where(i => i.GroundingVerified && !i.IsQuarantined)
                .GroupBy(i => i.WorkspaceId)
                .Select(g => new { WorkspaceId = g.Key, InsightCount = g.Count() })
                .ToDictionaryAsync(x => x.WorkspaceId, x => x.InsightCount, ct);

            var dealBreakerCounts = await db.FrictionDrivers
                .AsNoTracking()
                .Where(d => d.Severity == DriverSeverity.DealBreaker
                    && d.DecisionInsight != null
                    && d.DecisionInsight.GroundingVerified
                    && !d.DecisionInsight.IsQuarantined)
                .GroupBy(d => d.DecisionInsight!.WorkspaceId)
                .Select(g => new { WorkspaceId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.WorkspaceId, x => x.Count, ct);

            var dtos = workspaces
                .OrderByDescending(w => w.CreatedAt)
                .Select(w => new WorkspaceSummaryDto(
                    w.Id,
                    w.Name,
                    w.Slug,
                    w.Description,
                    w.DomainCategory,
                    w.CompliancePolicy,
                    w.CreatedAt,
                    counts.GetValueOrDefault(w.Id, 0),
                    dealBreakerCounts.GetValueOrDefault(w.Id, 0),
                    w.Entities.Select(e => new EntityDto(e.Id, e.Name, e.AliasesCsv, e.IsPrimaryBrand)).ToList(),
                    w.Sources.Select(s => new SourceConfigDto(
                        s.Id,
                        s.SourceType,
                        s.Label,
                        s.Target,
                        s.Enabled,
                        s.LastSyncedAt,
                        s.SyncState?.ConsecutiveFailures ?? 0,
                        s.SyncState?.LastErrorMessage)).ToList()))
                .ToList();

            return Results.Ok(dtos);
        });

        // Create a new generalizable workspace for ANY domain/category
        api.MapPost("/workspaces", async (CreateWorkspaceRequest req, AppDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Entities.Count == 0)
            {
                return Results.BadRequest(new { error = "Workspace Name and at least one TrackedEntity are required." });
            }

            var baseSlug = SlugRegex().Replace(req.Name.ToLowerInvariant().Trim(), "-").Trim('-');
            if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "workspace";
            var slug = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";

            var workspace = new Workspace
            {
                Name = req.Name.Trim(),
                Slug = slug,
                Description = req.Description?.Trim() ?? string.Empty,
                DomainCategory = string.IsNullOrWhiteSpace(req.DomainCategory) ? "General" : req.DomainCategory.Trim(),
                CompliancePolicy = req.CompliancePolicy,
                Entities = req.Entities.Select(e => new TrackedEntity
                {
                    Name = e.Name.Trim(),
                    AliasesCsv = e.AliasesCsv?.Trim() ?? string.Empty,
                    IsPrimaryBrand = e.IsPrimaryBrand
                }).ToList(),
                Sources = (req.Sources ?? []).Select(s => new SourceConfig
                {
                    SourceType = s.SourceType,
                    Label = s.Label.Trim(),
                    Target = s.Target.Trim(),
                    Enabled = true
                }).ToList()
            };

            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/workspaces/{workspace.Id}", new { workspace.Id, workspace.Slug });
        });

        // Add a compliant source connector to a workspace (with SSRF check for Discourse)
        api.MapPost("/workspaces/{workspaceId:guid}/sources", async (Guid workspaceId, AddSourceRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var exists = await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct);
            if (!exists) return Results.NotFound();

            if (req.SourceType == SourceType.Discourse)
            {
                var baseUrl = req.Target.Split('|', 2, StringSplitOptions.TrimEntries)[0];
                var (allowed, reason, _) = await SsrfGuard.ValidateExternalUrlAsync(baseUrl, ct);
                if (!allowed)
                {
                    return Results.BadRequest(new { error = $"Invalid Discourse URL: {reason}" });
                }
            }

            var source = new SourceConfig
            {
                WorkspaceId = workspaceId,
                SourceType = req.SourceType,
                Label = req.Label.Trim(),
                Target = req.Target.Trim(),
                Enabled = true
            };

            db.SourceConfigs.Add(source);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/workspaces/{workspaceId}/sources/{source.Id}",
                new SourceConfigDto(source.Id, source.SourceType, source.Label, source.Target, source.Enabled, source.LastSyncedAt));
        });

        // Toggle source enabled state
        api.MapPatch("/workspaces/{workspaceId:guid}/sources/{sourceId:guid}", async (
            Guid workspaceId,
            Guid sourceId,
            ToggleSourceRequest req,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var source = await db.SourceConfigs.FirstOrDefaultAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId, ct);
            if (source is null) return Results.NotFound();

            source.Enabled = req.Enabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SourceConfigDto(source.Id, source.SourceType, source.Label, source.Target, source.Enabled, source.LastSyncedAt));
        });

        // Delete a source connector
        api.MapDelete("/workspaces/{workspaceId:guid}/sources/{sourceId:guid}", async (
            Guid workspaceId,
            Guid sourceId,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var source = await db.SourceConfigs.FirstOrDefaultAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId, ct);
            if (source is null) return Results.NotFound();

            db.SourceConfigs.Remove(source);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Trigger async live ingestion across all enabled compliant connectors
        api.MapPost("/workspaces/{workspaceId:guid}/ingest/sync", async (
            Guid workspaceId,
            TriggerIngestionRequest? req,
            AppDbContext db,
            IIngestionQueue queue,
            CancellationToken ct) =>
        {
            var exists = await db.Workspaces.AnyAsync(w => w.Id == workspaceId, ct);
            if (!exists) return Results.NotFound();

            var job = new IngestionJob
            {
                WorkspaceId = workspaceId,
                Status = JobStatus.Queued,
                CurrentStage = "Queued",
                TriggerSource = "LiveConnectorSync"
            };

            db.IngestionJobs.Add(job);
            await db.SaveChangesAsync(ct);

            await queue.EnqueueAsync(new IngestionWorkItem(job.Id, workspaceId, req?.MaxItemsPerSource ?? 20), ct);

            return Results.Accepted($"/api/workspaces/{workspaceId}/jobs/{job.Id}/events", new IngestionJobDto(
                job.Id, job.Status, job.TriggerSource, job.CurrentStage, job.ProgressPercent,
                job.DocumentsFetched, job.DocumentsPassedFilter, job.InsightsExtracted,
                job.ErrorMessage, job.CreatedAt, job.CompletedAt));
        });

        // Native .NET 11 Server-Sent Events (SSE) live progress stream for an ingestion job
        api.MapGet("/workspaces/{workspaceId:guid}/jobs/{jobId:guid}/events", (
            Guid workspaceId,
            Guid jobId,
            IJobProgressBus bus,
            CancellationToken ct) =>
        {
            return TypedResults.ServerSentEvents(bus.SubscribeAsync(jobId, ct), eventType: "job-progress");
        });

        // Instant manual paste ingestion
        api.MapPost("/workspaces/{workspaceId:guid}/ingest/manual", async (
            Guid workspaceId,
            ManualIngestRequest req,
            AppDbContext db,
            ExtractionPipeline pipeline,
            CancellationToken ct) =>
        {
            var workspace = await db.Workspaces
                .Include(w => w.Entities)
                .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

            if (workspace is null) return Results.NotFound();
            if (req.Items.Count == 0) return Results.BadRequest(new { error = "Provide at least one item to ingest." });

            var docs = req.Items
                .Where(i => !string.IsNullOrWhiteSpace(i.Body))
                .Select(i => new FetchedDocument(
                    req.SourceType,
                    $"manual-{Guid.NewGuid():N}",
                    string.IsNullOrWhiteSpace(i.Title) ? "Manual Thread Import" : i.Title.Trim(),
                    i.Body.Trim(),
                    i.SourceUrl?.Trim() ?? string.Empty,
                    i.PublishedAt ?? DateTimeOffset.UtcNow))
                .ToList();

            var summary = await pipeline.ProcessDocumentsAsync(workspace, docs, ct);
            return Results.Ok(summary);
        });

        // Bulk CSV / JSONL first-party import with mandatory ownership attestation
        api.MapPost("/workspaces/{workspaceId:guid}/ingest/csv", async (
            Guid workspaceId,
            BulkCsvIngestRequest req,
            AppDbContext db,
            ExtractionPipeline pipeline,
            CancellationToken ct) =>
        {
            if (!req.OwnershipConfirmed)
            {
                return Results.UnprocessableEntity(new { error = "OwnershipConfirmed must be true to import first-party CSV/JSONL data." });
            }

            var workspace = await db.Workspaces
                .Include(w => w.Entities)
                .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

            if (workspace is null) return Results.NotFound();

            var parsed = CsvJsonlImportService.Parse(req.CsvContent, req.Format, maxRows: 500);
            if (parsed.Documents.Count == 0)
            {
                return Results.BadRequest(new { error = "No valid rows found in CSV/JSONL payload." });
            }

            db.DataImportAttestations.Add(new DataImportAttestation
            {
                WorkspaceId = workspaceId,
                LegalBasis = string.IsNullOrWhiteSpace(req.LegalBasis) ? "InternalFeedback" : req.LegalBasis.Trim(),
                OwnershipConfirmed = true,
                PiiRedactionApplied = true,
                RowCount = parsed.Documents.Count
            });

            var summary = await pipeline.ProcessDocumentsAsync(workspace, parsed.Documents, ct);
            return Results.Ok(summary);
        });

        // List recent ingestion jobs
        api.MapGet("/workspaces/{workspaceId:guid}/jobs", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var jobs = (await db.IngestionJobs
                .AsNoTracking()
                .Where(j => j.WorkspaceId == workspaceId)
                .ToListAsync(ct))
                .OrderByDescending(j => j.CreatedAt)
                .Take(15)
                .Select(j => new IngestionJobDto(
                    j.Id, j.Status, j.TriggerSource, j.CurrentStage, j.ProgressPercent,
                    j.DocumentsFetched, j.DocumentsPassedFilter, j.InsightsExtracted,
                    j.ErrorMessage, j.CreatedAt, j.CompletedAt))
                .ToList();

            return Results.Ok(jobs);
        });

        // List dead-letter errors for a workspace
        api.MapGet("/workspaces/{workspaceId:guid}/dead-letters", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var items = (await db.IngestionDeadLetters
                .AsNoTracking()
                .Where(d => d.WorkspaceId == workspaceId)
                .ToListAsync(ct))
                .OrderByDescending(d => d.OccurredAt)
                .Take(25)
                .Select(d => new DeadLetterDto(d.Id, d.SourceType, d.Stage, d.ExternalId, d.ErrorMessage, d.OccurredAt))
                .ToList();

            return Results.Ok(items);
        });

        // List extracted insights with verbatim evidence, search, and grounding/quarantine filter
        api.MapGet("/workspaces/{workspaceId:guid}/insights", async (
            Guid workspaceId,
            string? entity,
            DriverSeverity? severity,
            string? q,
            bool? includeQuarantined,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var query = db.DecisionInsights
                .AsNoTracking()
                .Include(i => i.Drivers)
                .Where(i => i.WorkspaceId == workspaceId);

            if (includeQuarantined != true)
            {
                query = query.Where(i => i.GroundingVerified && !i.IsQuarantined);
            }

            if (!string.IsNullOrWhiteSpace(entity))
            {
                query = query.Where(i => i.PrimaryEntity == entity || i.CompetitorsComparedCsv.Contains(entity));
            }

            if (severity.HasValue)
            {
                query = query.Where(i => i.Drivers.Any(d => d.Severity == severity.Value));
            }

            var insights = (await query.ToListAsync(ct))
                .OrderByDescending(i => i.ObservedAt)
                .AsEnumerable();

            if (!string.IsNullOrWhiteSpace(q))
            {
                var term = q.Trim();
                insights = insights.Where(i =>
                    i.VerbatimEvidence.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.UnmetRequirement.Contains(term, StringComparison.OrdinalIgnoreCase)
                    || i.Drivers.Any(d => d.DisplayLabel.Contains(term, StringComparison.OrdinalIgnoreCase)
                        || d.CanonicalTheme.Contains(term, StringComparison.OrdinalIgnoreCase)));
            }

            var dtos = insights.Take(100).Select(i => new InsightDetailDto(
                i.Id,
                i.SourceType,
                i.SourceUrl,
                i.SourceTitle,
                i.ObservedAt,
                i.PrimaryEntity,
                i.CompetitorsComparedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                i.Outcome,
                i.Summary,
                i.UnmetRequirement,
                i.VerbatimEvidence,
                i.GroundingVerified,
                i.GroundingScore,
                i.GroundingTier,
                i.IsQuarantined,
                i.Confidence,
                i.ExtractorModel,
                i.UserVerifiedAccurate,
                i.Drivers.Select(d => new FrictionDriverDto(
                    d.Id, d.TargetEntity, d.Category, d.CanonicalTheme,
                    d.DisplayLabel, d.RawPhrase, d.Severity, d.Polarity)).ToList()))
                .ToList();

            return Results.Ok(dtos);
        });

        // Human-in-the-loop precision feedback flag
        api.MapPatch("/insights/{insightId:guid}/feedback", async (
            Guid insightId,
            FeedbackFlagRequest req,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var insight = await db.DecisionInsights.FirstOrDefaultAsync(i => i.Id == insightId, ct);
            if (insight is null) return Results.NotFound();

            insight.UserVerifiedAccurate = req.Accurate;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { insight.Id, insight.UserVerifiedAccurate });
        });

        // Head-to-head Win/Loss Matrix
        api.MapGet("/workspaces/{workspaceId:guid}/matrix", async (Guid workspaceId, IntelligenceService intel, CancellationToken ct) =>
        {
            var matrix = await intel.BuildWinLossMatrixAsync(workspaceId, ct);
            return matrix is null ? Results.NotFound() : Results.Ok(matrix);
        });

        // Ranked Friction Drivers & Advantages
        api.MapGet("/workspaces/{workspaceId:guid}/friction", async (
            Guid workspaceId,
            DriverPolarity? polarity,
            IntelligenceService intel,
            CancellationToken ct) =>
        {
            var ranking = await intel.GetFrictionRankingAsync(workspaceId, polarity, ct);
            return Results.Ok(ranking);
        });

        // Auto-generated Weekly Intelligence Brief (Markdown)
        api.MapGet("/workspaces/{workspaceId:guid}/brief", async (Guid workspaceId, IntelligenceService intel, CancellationToken ct) =>
        {
            var brief = await intel.GenerateWeeklyBriefAsync(workspaceId, ct);
            return brief is null ? Results.NotFound() : Results.Ok(brief);
        });

        // Standalone Print-to-PDF-Ready Trojan Horse Executive Teardown Report (HTML)
        api.MapGet("/workspaces/{workspaceId:guid}/report/html", async (
            Guid workspaceId,
            TeardownReportBuilder builder,
            CancellationToken ct) =>
        {
            var html = await builder.BuildHtmlReportAsync(workspaceId, ct);
            return html is null ? Results.NotFound() : Results.Content(html, "text/html; charset=utf-8");
        });

        // Canonical Themes & Alias Management
        api.MapGet("/workspaces/{workspaceId:guid}/themes", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var themes = await db.CanonicalThemes
                .AsNoTracking()
                .Include(t => t.Aliases)
                .Where(t => t.WorkspaceId == workspaceId && t.MergedIntoThemeId == null)
                .ToListAsync(ct);

            var dtos = themes
                .OrderBy(t => t.DisplayLabel)
                .Select(t => new CanonicalThemeDto(t.Id, t.Slug, t.DisplayLabel, t.Category, t.Aliases.Select(a => a.NormalizedPhrase).ToList()))
                .ToList();

            return Results.Ok(dtos);
        });

        api.MapPost("/workspaces/{workspaceId:guid}/themes/{themeId:guid}/aliases", async (
            Guid workspaceId,
            Guid themeId,
            AddAliasRequest req,
            AppDbContext db,
            CancellationToken ct) =>
        {
            var theme = await db.CanonicalThemes.Include(t => t.Aliases).FirstOrDefaultAsync(t => t.Id == themeId && t.WorkspaceId == workspaceId, ct);
            if (theme is null) return Results.NotFound();

            var norm = CanonicalTaxonomyResolver.NormalizePhrase(req.Phrase);
            if (string.IsNullOrWhiteSpace(norm)) return Results.BadRequest(new { error = "Phrase is required." });

            if (!theme.Aliases.Any(a => a.NormalizedPhrase == norm))
            {
                db.ThemeAliases.Add(new ThemeAlias
                {
                    WorkspaceId = workspaceId,
                    CanonicalThemeId = themeId,
                    NormalizedPhrase = norm,
                    Source = "UserEdit"
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(new { themeId, addedAlias = norm });
        });

        // Compliance Right-to-Erasure & Takedown Tombstone Endpoint
        api.MapPost("/compliance/takedowns", async (TakedownRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var urlHash = ExtractionPipeline.ComputeNormalizedUrlHash(req.SourceUrl);
            if (string.IsNullOrEmpty(urlHash) && string.IsNullOrWhiteSpace(req.ExternalId) && req.RawDocumentId is null)
            {
                return Results.BadRequest(new { error = "Provide SourceUrl, ExternalId, or RawDocumentId." });
            }

            var docsQuery = db.RawDocuments.AsQueryable();
            if (req.WorkspaceId.HasValue) docsQuery = docsQuery.Where(d => d.WorkspaceId == req.WorkspaceId.Value);

            var matchingDocs = await docsQuery.Where(d =>
                (req.RawDocumentId.HasValue && d.Id == req.RawDocumentId.Value)
                || (!string.IsNullOrEmpty(urlHash) && d.NormalizedUrlHash == urlHash)
                || (!string.IsNullOrWhiteSpace(req.ExternalId) && d.ExternalId == req.ExternalId))
                .ToListAsync(ct);

            var docIds = matchingDocs.Select(d => d.Id).ToHashSet();
            var matchingInsights = await db.DecisionInsights
                .Where(i => (i.RawDocumentId.HasValue && docIds.Contains(i.RawDocumentId.Value))
                    || (!string.IsNullOrWhiteSpace(req.SourceUrl) && i.SourceUrl == req.SourceUrl))
                .ToListAsync(ct);

            var contentHash = matchingDocs.FirstOrDefault()?.ContentHash;

            db.DecisionInsights.RemoveRange(matchingInsights);
            db.RawDocuments.RemoveRange(matchingDocs);

            var tombstone = new TakedownTombstone
            {
                WorkspaceId = req.WorkspaceId,
                NormalizedUrlHash = string.IsNullOrEmpty(urlHash) ? null : urlHash,
                ContentHash = contentHash,
                ExternalId = req.ExternalId,
                Reason = req.Reason,
                RequestedBy = req.RequestedBy,
                PurgedDocumentsCount = matchingDocs.Count,
                PurgedInsightsCount = matchingInsights.Count
            };

            db.TakedownTombstones.Add(tombstone);
            await db.SaveChangesAsync(ct);

            return Results.Ok(new TakedownResponse(tombstone.Id, matchingDocs.Count, matchingInsights.Count));
        });

        // LLM Token & Cost Usage Ledger Summary
        api.MapGet("/workspaces/{workspaceId:guid}/usage", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var entries = await db.UsageLedger
                .AsNoTracking()
                .Where(u => u.WorkspaceId == workspaceId)
                .ToListAsync(ct);

            return Results.Ok(new UsageSummaryDto(
                entries.Count,
                entries.Sum(e => (long)e.PromptTokens),
                entries.Sum(e => (long)e.CompletionTokens),
                entries.Sum(e => e.EstimatedCostUsd)));
        });
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex SlugRegex();
}
