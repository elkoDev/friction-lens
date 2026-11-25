using System.Text.RegularExpressions;
using FrictionLens.Api.Analytics;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using FrictionLens.Api.Ingestion;
using FrictionLens.Api.Workflows;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Endpoints;

public static partial class ApiEndpoints
{
    public static void MapFrictionLensApi(this WebApplication app)
    {
        // Cloud-ready liveness & readiness health probes
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", timestamp = DateTimeOffset.UtcNow }));
        app.MapGet("/health/ready", async (AppDbContext db, CancellationToken ct) =>
        {
            var canConnect = await db.Database.CanConnectAsync(ct);
            return canConnect
                ? Results.Ok(new { status = "ready", timestamp = DateTimeOffset.UtcNow })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        });

        var api = app.MapGroup("/api");

        // System health & active provider configuration
        api.MapGet("/status", (IConfiguration cfg, IExtractionProvider provider, IEmbeddingProvider embedder) =>
        {
            var dbProvider = cfg["Database:Provider"] ?? "Sqlite";
            var ollamaUrl = cfg["Extraction:OllamaBaseUrl"];
            var cloudKey = cfg["Extraction:ApiKey"] ?? Environment.GetEnvironmentVariable("LLM_API_KEY");
            var ytKey = cfg["Connectors:YouTubeApiKey"] ?? Environment.GetEnvironmentVariable("YOUTUBE_API_KEY");

            return Results.Ok(new SystemStatusDto(
                dbProvider,
                provider.ProviderName,
                provider.ModelName,
                $"{embedder.ProviderName} ({embedder.ModelName})",
                !string.IsNullOrWhiteSpace(ollamaUrl),
                !string.IsNullOrWhiteSpace(cloudKey),
                !string.IsNullOrWhiteSpace(ytKey),
                "0.3.0-phase2"));
        });

        // Out-of-the-box Generalizable Domain Packs
        api.MapGet("/domain-packs", () => Results.Ok(DomainPackRegistry.Packs));

        // List all workspaces (projected counts)
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
                    w.DomainPackId,
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

        // Create a new generalizable workspace seeded with a chosen Domain Pack
        api.MapPost("/workspaces", async (
            CreateWorkspaceRequest req,
            AppDbContext db,
            CanonicalTaxonomyResolver taxonomyResolver,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name) || req.Entities.Count == 0)
            {
                return Results.BadRequest(new { error = "Workspace Name and at least one TrackedEntity are required." });
            }

            var pack = DomainPackRegistry.GetOrDefault(req.DomainPackId);
            var baseSlug = SlugRegex().Replace(req.Name.ToLowerInvariant().Trim(), "-").Trim('-');
            if (string.IsNullOrWhiteSpace(baseSlug)) baseSlug = "workspace";
            var slug = $"{baseSlug}-{Guid.NewGuid().ToString("N")[..6]}";

            var workspace = new Workspace
            {
                Name = req.Name.Trim(),
                Slug = slug,
                Description = req.Description?.Trim() ?? string.Empty,
                DomainCategory = string.IsNullOrWhiteSpace(req.DomainCategory) ? pack.DefaultDomainCategory : req.DomainCategory.Trim(),
                DomainPackId = pack.PackId,
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

            await taxonomyResolver.SeedDomainPackThemesAsync(workspace, pack, ct);

            db.Workspaces.Add(workspace);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/workspaces/{workspace.Id}", new { workspace.Id, workspace.Slug, domainPack = pack.PackId });
        });

        // Add a compliant source connector (with SSRF check for Discourse)
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

        api.MapPatch("/workspaces/{workspaceId:guid}/sources/{sourceId:guid}", async (
            Guid workspaceId, Guid sourceId, ToggleSourceRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var source = await db.SourceConfigs.FirstOrDefaultAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId, ct);
            if (source is null) return Results.NotFound();

            source.Enabled = req.Enabled;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new SourceConfigDto(source.Id, source.SourceType, source.Label, source.Target, source.Enabled, source.LastSyncedAt));
        });

        api.MapDelete("/workspaces/{workspaceId:guid}/sources/{sourceId:guid}", async (
            Guid workspaceId, Guid sourceId, AppDbContext db, CancellationToken ct) =>
        {
            var source = await db.SourceConfigs.FirstOrDefaultAsync(s => s.Id == sourceId && s.WorkspaceId == workspaceId, ct);
            if (source is null) return Results.NotFound();

            db.SourceConfigs.Remove(source);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Async live sync + SSE progress stream
        api.MapPost("/workspaces/{workspaceId:guid}/ingest/sync", async (
            Guid workspaceId, TriggerIngestionRequest? req, AppDbContext db, IIngestionQueue queue, CancellationToken ct) =>
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

        api.MapGet("/workspaces/{workspaceId:guid}/jobs/{jobId:guid}/events", (
            Guid workspaceId, Guid jobId, IJobProgressBus bus, CancellationToken ct)
            => TypedResults.ServerSentEvents(bus.SubscribeAsync(jobId, ct), eventType: "job-progress"));

        // Manual paste & Bulk CSV/JSONL import
        api.MapPost("/workspaces/{workspaceId:guid}/ingest/manual", async (
            Guid workspaceId, ManualIngestRequest req, AppDbContext db, ExtractionPipeline pipeline, CancellationToken ct) =>
        {
            var workspace = await db.Workspaces.Include(w => w.Entities).FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
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

            return Results.Ok(await pipeline.ProcessDocumentsAsync(workspace, docs, ct));
        });

        api.MapPost("/workspaces/{workspaceId:guid}/ingest/csv", async (
            Guid workspaceId, BulkCsvIngestRequest req, AppDbContext db, ExtractionPipeline pipeline, CancellationToken ct) =>
        {
            if (!req.OwnershipConfirmed)
                return Results.UnprocessableEntity(new { error = "OwnershipConfirmed must be true to import first-party CSV/JSONL data." });

            var workspace = await db.Workspaces.Include(w => w.Entities).FirstOrDefaultAsync(w => w.Id == workspaceId, ct);
            if (workspace is null) return Results.NotFound();

            var parsed = CsvJsonlImportService.Parse(req.CsvContent, req.Format, maxRows: 500);
            if (parsed.Documents.Count == 0)
                return Results.BadRequest(new { error = "No valid rows found in CSV/JSONL payload." });

            db.DataImportAttestations.Add(new DataImportAttestation
            {
                WorkspaceId = workspaceId,
                LegalBasis = string.IsNullOrWhiteSpace(req.LegalBasis) ? "InternalFeedback" : req.LegalBasis.Trim(),
                OwnershipConfirmed = true,
                PiiRedactionApplied = true,
                RowCount = parsed.Documents.Count
            });

            return Results.Ok(await pipeline.ProcessDocumentsAsync(workspace, parsed.Documents, ct));
        });

        // Jobs & Dead Letters
        api.MapGet("/workspaces/{workspaceId:guid}/jobs", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var jobs = (await db.IngestionJobs.AsNoTracking().Where(j => j.WorkspaceId == workspaceId).ToListAsync(ct))
                .OrderByDescending(j => j.CreatedAt)
                .Take(15)
                .Select(j => new IngestionJobDto(
                    j.Id, j.Status, j.TriggerSource, j.CurrentStage, j.ProgressPercent,
                    j.DocumentsFetched, j.DocumentsPassedFilter, j.InsightsExtracted,
                    j.ErrorMessage, j.CreatedAt, j.CompletedAt))
                .ToList();
            return Results.Ok(jobs);
        });

        api.MapGet("/workspaces/{workspaceId:guid}/dead-letters", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var items = (await db.IngestionDeadLetters.AsNoTracking().Where(d => d.WorkspaceId == workspaceId).ToListAsync(ct))
                .OrderByDescending(d => d.OccurredAt)
                .Take(25)
                .Select(d => new DeadLetterDto(d.Id, d.SourceType, d.Stage, d.ExternalId, d.ErrorMessage, d.OccurredAt))
                .ToList();
            return Results.Ok(items);
        });

        // Evidence Room
        api.MapGet("/workspaces/{workspaceId:guid}/insights", async (
            Guid workspaceId, string? entity, DriverSeverity? severity, string? q, bool? includeQuarantined,
            AppDbContext db, CancellationToken ct) =>
        {
            var query = db.DecisionInsights.AsNoTracking().Include(i => i.Drivers).Where(i => i.WorkspaceId == workspaceId);
            if (includeQuarantined != true) query = query.Where(i => i.GroundingVerified && !i.IsQuarantined);
            if (!string.IsNullOrWhiteSpace(entity)) query = query.Where(i => i.PrimaryEntity == entity || i.CompetitorsComparedCsv.Contains(entity));
            if (severity.HasValue) query = query.Where(i => i.Drivers.Any(d => d.Severity == severity.Value));

            var insights = (await query.ToListAsync(ct)).OrderByDescending(i => i.ObservedAt).AsEnumerable();
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

            return Results.Ok(insights.Take(100).Select(i => new InsightDetailDto(
                i.Id, i.SourceType, i.SourceUrl, i.SourceTitle, i.ObservedAt, i.PrimaryEntity,
                i.CompetitorsComparedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                i.Outcome, i.Summary, i.UnmetRequirement, i.VerbatimEvidence, i.GroundingVerified,
                i.GroundingScore, i.GroundingTier, i.IsQuarantined, i.Confidence, i.ExtractorModel,
                i.UserVerifiedAccurate,
                i.Drivers.Select(d => new FrictionDriverDto(
                    d.Id, d.TargetEntity, d.Category, d.CanonicalTheme,
                    d.DisplayLabel, d.RawPhrase, d.Severity, d.Polarity)).ToList())).ToList());
        });

        api.MapPatch("/insights/{insightId:guid}/feedback", async (
            Guid insightId, FeedbackFlagRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var insight = await db.DecisionInsights.FirstOrDefaultAsync(i => i.Id == insightId, ct);
            if (insight is null) return Results.NotFound();
            insight.UserVerifiedAccurate = req.Accurate;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { insight.Id, insight.UserVerifiedAccurate });
        });

        // Analytics: Matrix, Friction, Trends, Brief, Teardown HTML, Sales Battlecard
        api.MapGet("/workspaces/{workspaceId:guid}/matrix", async (Guid workspaceId, IntelligenceService intel, CancellationToken ct) =>
        {
            var matrix = await intel.BuildWinLossMatrixAsync(workspaceId, ct);
            return matrix is null ? Results.NotFound() : Results.Ok(matrix);
        });

        api.MapGet("/workspaces/{workspaceId:guid}/friction", async (
            Guid workspaceId, DriverPolarity? polarity, IntelligenceService intel, CancellationToken ct)
            => Results.Ok(await intel.GetFrictionRankingAsync(workspaceId, polarity, ct)));

        api.MapGet("/workspaces/{workspaceId:guid}/trends", async (
            Guid workspaceId, TrendVelocityService trends, CancellationToken ct)
            => Results.Ok(await trends.ComputeVelocityAsync(workspaceId, ct)));

        api.MapGet("/workspaces/{workspaceId:guid}/brief", async (Guid workspaceId, IntelligenceService intel, CancellationToken ct) =>
        {
            var brief = await intel.GenerateWeeklyBriefAsync(workspaceId, ct);
            return brief is null ? Results.NotFound() : Results.Ok(brief);
        });

        api.MapGet("/workspaces/{workspaceId:guid}/report/html", async (
            Guid workspaceId, TeardownReportBuilder builder, CancellationToken ct) =>
        {
            var html = await builder.BuildHtmlReportAsync(workspaceId, ct);
            return html is null ? Results.NotFound() : Results.Content(html, "text/html; charset=utf-8");
        });

        api.MapPost("/workspaces/{workspaceId:guid}/exports/battlecard", async (
            Guid workspaceId, BattlecardRequest req, BattlecardBuilder builder, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.PrimaryEntity) || string.IsNullOrWhiteSpace(req.CompetitorEntity))
                return Results.BadRequest(new { error = "PrimaryEntity and CompetitorEntity are required." });

            var card = await builder.BuildAsync(workspaceId, req.PrimaryEntity.Trim(), req.CompetitorEntity.Trim(), ct);
            return card is null ? Results.NotFound() : Results.Ok(card);
        });

        // Phase 2 Canonical Taxonomy Management (List, Suggestions, Alias, Rename, Merge)
        api.MapGet("/workspaces/{workspaceId:guid}/themes", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var themes = await db.CanonicalThemes
                .AsNoTracking()
                .Include(t => t.Aliases)
                .Where(t => t.WorkspaceId == workspaceId && t.MergedIntoThemeId == null)
                .ToListAsync(ct);

            var driverCounts = await db.FrictionDrivers
                .AsNoTracking()
                .Where(d => d.DecisionInsight != null && d.DecisionInsight.WorkspaceId == workspaceId)
                .GroupBy(d => d.CanonicalTheme)
                .Select(g => new { Slug = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Slug, x => x.Count, ct);

            return Results.Ok(themes
                .OrderByDescending(t => driverCounts.GetValueOrDefault(t.Slug, 0))
                .ThenBy(t => t.DisplayLabel)
                .Select(t => new CanonicalThemeDto(
                    t.Id, t.Slug, t.DisplayLabel, t.Category,
                    driverCounts.GetValueOrDefault(t.Slug, 0),
                    t.Aliases.Select(a => a.NormalizedPhrase).ToList()))
                .ToList());
        });

        api.MapGet("/workspaces/{workspaceId:guid}/themes/suggestions", async (
            Guid workspaceId, CanonicalTaxonomyResolver resolver, CancellationToken ct)
            => Results.Ok(await resolver.FindMergeSuggestionsAsync(workspaceId, ct)));

        api.MapPost("/workspaces/{workspaceId:guid}/themes/merge", async (
            Guid workspaceId, MergeThemesRequest req, CanonicalTaxonomyResolver resolver, CancellationToken ct) =>
        {
            var ok = await resolver.MergeThemesAsync(workspaceId, req.SourceThemeIds, req.TargetThemeId, ct);
            return ok ? Results.Ok(new { merged = true, targetThemeId = req.TargetThemeId }) : Results.BadRequest(new { error = "Invalid source or target theme IDs." });
        });

        api.MapPatch("/workspaces/{workspaceId:guid}/themes/{themeId:guid}", async (
            Guid workspaceId, Guid themeId, RenameThemeRequest req, CanonicalTaxonomyResolver resolver, CancellationToken ct) =>
        {
            var ok = await resolver.RenameThemeAsync(workspaceId, themeId, req.DisplayLabel, req.Category, ct);
            return ok ? Results.Ok(new { renamed = true, themeId }) : Results.NotFound();
        });

        api.MapPost("/workspaces/{workspaceId:guid}/themes/{themeId:guid}/aliases", async (
            Guid workspaceId, Guid themeId, AddAliasRequest req, AppDbContext db, CancellationToken ct) =>
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

        // Slack / Push Notification Channels
        api.MapGet("/workspaces/{workspaceId:guid}/notifications", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var channels = await db.NotificationChannels.AsNoTracking().Where(c => c.WorkspaceId == workspaceId).ToListAsync(ct);
            return Results.Ok(channels.Select(c => new NotificationChannelDto(
                c.Id, c.ChannelType, c.Label, MaskUrl(c.WebhookUrl), c.Enabled, c.LastDeliveredAt)).ToList());
        });

        api.MapPost("/workspaces/{workspaceId:guid}/notifications", async (
            Guid workspaceId, CreateNotificationChannelRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var (allowed, reason, _) = await SsrfGuard.ValidateExternalUrlAsync(req.WebhookUrl, ct);
            if (!allowed) return Results.BadRequest(new { error = $"Webhook URL rejected: {reason}" });

            var ch = new NotificationChannel
            {
                WorkspaceId = workspaceId,
                ChannelType = string.IsNullOrWhiteSpace(req.ChannelType) ? "SlackWebhook" : req.ChannelType.Trim(),
                Label = req.Label.Trim(),
                WebhookUrl = req.WebhookUrl.Trim(),
                Enabled = true
            };
            db.NotificationChannels.Add(ch);
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/workspaces/{workspaceId}/notifications/{ch.Id}",
                new NotificationChannelDto(ch.Id, ch.ChannelType, ch.Label, MaskUrl(ch.WebhookUrl), ch.Enabled, ch.LastDeliveredAt));
        });

        api.MapPost("/workspaces/{workspaceId:guid}/notifications/slack/digest", async (
            Guid workspaceId, SlackWebhookService slack, CancellationToken ct) =>
        {
            var (sent, msg) = await slack.SendWeeklyDigestAsync(workspaceId, null, ct);
            return sent ? Results.Ok(new { sent, message = msg }) : Results.BadRequest(new { sent, message = msg });
        });

        // Compliance Takedown Tombstone
        api.MapPost("/compliance/takedowns", async (TakedownRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var urlHash = ExtractionPipeline.ComputeNormalizedUrlHash(req.SourceUrl);
            if (string.IsNullOrEmpty(urlHash) && string.IsNullOrWhiteSpace(req.ExternalId) && req.RawDocumentId is null)
                return Results.BadRequest(new { error = "Provide SourceUrl, ExternalId, or RawDocumentId." });

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

        api.MapGet("/workspaces/{workspaceId:guid}/usage", async (Guid workspaceId, AppDbContext db, CancellationToken ct) =>
        {
            var entries = await db.UsageLedger.AsNoTracking().Where(u => u.WorkspaceId == workspaceId).ToListAsync(ct);
            return Results.Ok(new UsageSummaryDto(
                entries.Count,
                entries.Sum(e => (long)e.PromptTokens),
                entries.Sum(e => (long)e.CompletionTokens),
                entries.Sum(e => e.EstimatedCostUsd)));
        });
    }

    private static string MaskUrl(string url)
        => url.Length <= 28 ? url : $"{url[..24]}...{url[^4..]}";

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.Compiled)]
    private static partial Regex SlugRegex();
}
