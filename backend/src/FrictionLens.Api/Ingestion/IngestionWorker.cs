using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Ingestion;

public sealed record IngestionWorkItem(Guid JobId, Guid WorkspaceId, int MaxItemsPerSource);

public interface IIngestionQueue
{
    ValueTask EnqueueAsync(IngestionWorkItem item, CancellationToken ct = default);
    IAsyncEnumerable<IngestionWorkItem> DequeueAllAsync(CancellationToken ct);
}

public sealed class IngestionQueue : IIngestionQueue
{
    private readonly Channel<IngestionWorkItem> _channel = Channel.CreateUnbounded<IngestionWorkItem>();

    public ValueTask EnqueueAsync(IngestionWorkItem item, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(item, ct);

    public IAsyncEnumerable<IngestionWorkItem> DequeueAllAsync(CancellationToken ct)
        => _channel.Reader.ReadAllAsync(ct);
}

public interface IJobProgressBus
{
    void Publish(JobProgressEvent evt);
    IAsyncEnumerable<JobProgressEvent> SubscribeAsync(Guid jobId, CancellationToken ct);
}

/// <summary>
/// In-memory per-job progress fan-out bus powering .NET 11 native SSE (TypedResults.ServerSentEvents).
/// </summary>
public sealed class JobProgressBus : IJobProgressBus
{
    private readonly ConcurrentDictionary<Guid, ConcurrentBag<Channel<JobProgressEvent>>> _subscribers = new();

    public void Publish(JobProgressEvent evt)
    {
        if (!_subscribers.TryGetValue(evt.JobId, out var channels)) return;
        foreach (var ch in channels)
        {
            ch.Writer.TryWrite(evt);
            if (evt.Status is JobStatus.Completed or JobStatus.Failed)
            {
                ch.Writer.TryComplete();
            }
        }
    }

    public async IAsyncEnumerable<JobProgressEvent> SubscribeAsync(Guid jobId, [EnumeratorCancellation] CancellationToken ct)
    {
        var ch = Channel.CreateUnbounded<JobProgressEvent>();
        var bag = _subscribers.GetOrAdd(jobId, _ => []);
        bag.Add(ch);

        try
        {
            await foreach (var evt in ch.Reader.ReadAllAsync(ct))
            {
                yield return evt;
            }
        }
        finally
        {
            ch.Writer.TryComplete();
        }
    }
}

public sealed class IngestionWorker(
    IIngestionQueue queue,
    IJobProgressBus progressBus,
    IServiceScopeFactory scopeFactory,
    ILogger<IngestionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("FrictionLens IngestionWorker started.");
        await RecoverOrphanedJobsAsync(stoppingToken);

        await foreach (var item in queue.DequeueAllAsync(stoppingToken))
        {
            await ProcessJobAsync(item, stoppingToken);
        }
    }

    private async Task RecoverOrphanedJobsAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var orphans = await db.IngestionJobs
                .Where(j => j.Status == JobStatus.Running)
                .ToListAsync(ct);

            foreach (var job in orphans)
            {
                job.Status = JobStatus.Failed;
                job.CurrentStage = "RecoveredAfterRestart";
                job.ErrorMessage = "Process restarted while job was running.";
                job.CompletedAt = DateTimeOffset.UtcNow;
            }

            if (orphans.Count > 0)
            {
                await db.SaveChangesAsync(ct);
                logger.LogWarning("Recovered {Count} orphaned running ingestion job(s) on startup.", orphans.Count);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Orphan job recovery skipped.");
        }
    }

    private async Task ProcessJobAsync(IngestionWorkItem item, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var connectors = scope.ServiceProvider.GetServices<ISourceConnector>().ToDictionary(c => c.SupportedSource);
        var pipeline = scope.ServiceProvider.GetRequiredService<ExtractionPipeline>();

        var job = await db.IngestionJobs.FirstOrDefaultAsync(j => j.Id == item.JobId, ct);
        if (job is null) return;

        try
        {
            job.Status = JobStatus.Running;
            job.AttemptCount++;
            job.CurrentStage = "Fetching";
            job.ProgressPercent = 10;
            await db.SaveChangesAsync(ct);
            Publish(job, "Starting compliant connector sync...");

            var workspace = await db.Workspaces
                .Include(w => w.Entities)
                .Include(w => w.Sources)
                    .ThenInclude(s => s.SyncState)
                .FirstOrDefaultAsync(w => w.Id == item.WorkspaceId, ct);

            if (workspace is null)
            {
                job.Status = JobStatus.Failed;
                job.CurrentStage = "Failed";
                job.ErrorMessage = "Workspace not found.";
                job.CompletedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
                Publish(job, job.ErrorMessage);
                return;
            }

            var enabledSources = workspace.Sources.Where(s => s.Enabled).ToList();
            var fetchedDocs = new List<FetchedDocument>();
            var pendingCheckpoints = new List<(SourceConfig Source, SyncCheckpoint Checkpoint)>();

            var idx = 0;
            foreach (var source in enabledSources)
            {
                idx++;
                if (!connectors.TryGetValue(source.SourceType, out var connector)) continue;

                var syncState = source.SyncState ?? new SourceSyncState { SourceConfigId = source.Id, SourceConfig = source };
                if (source.SyncState is null)
                {
                    source.SyncState = syncState;
                    db.SourceSyncStates.Add(syncState);
                }

                var checkpoint = new SyncCheckpoint(
                    syncState.CursorTimestamp,
                    syncState.LastExternalId,
                    syncState.CursorToken,
                    syncState.LastEtag);

                try
                {
                    Publish(job, $"Fetching {source.Label} ({idx}/{enabledSources.Count})...");
                    var fetchResult = await connector.FetchAsync(
                        new ConnectorFetchContext(source, workspace.Entities, item.MaxItemsPerSource, checkpoint),
                        ct);

                    fetchedDocs.AddRange(fetchResult.Documents);
                    pendingCheckpoints.Add((source, fetchResult.NextCheckpoint));
                    syncState.ConsecutiveFailures = 0;
                    syncState.LastErrorMessage = fetchResult.Warning;
                    source.LastSyncedAt = DateTimeOffset.UtcNow;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Connector {SourceType} failed for target '{Target}'", source.SourceType, source.Target);
                    syncState.ConsecutiveFailures++;
                    syncState.LastErrorMessage = ex.Message;
                    db.IngestionDeadLetters.Add(new IngestionDeadLetter
                    {
                        WorkspaceId = workspace.Id,
                        IngestionJobId = job.Id,
                        SourceType = source.SourceType,
                        Stage = "Fetch",
                        ExternalId = source.Target,
                        ErrorMessage = ex.Message
                    });
                }

                job.DocumentsFetched = fetchedDocs.Count;
                job.ProgressPercent = 10 + (int)(45.0 * idx / Math.Max(enabledSources.Count, 1));
                await db.SaveChangesAsync(ct);
            }

            job.CurrentStage = "Extracting";
            job.ProgressPercent = 65;
            await db.SaveChangesAsync(ct);
            Publish(job, $"Running 4-stage extraction & grounding pipeline on {fetchedDocs.Count} fetched doc(s)...");

            var summary = await pipeline.ProcessDocumentsAsync(workspace, fetchedDocs, ct, job.Id);

            // Commit sync checkpoints only after extraction commits cleanly
            foreach (var (source, nextCp) in pendingCheckpoints)
            {
                if (source.SyncState is not null)
                {
                    source.SyncState.CursorTimestamp = nextCp.CursorTimestamp;
                    source.SyncState.LastExternalId = nextCp.LastExternalId;
                    source.SyncState.CursorToken = nextCp.CursorToken;
                    source.SyncState.LastEtag = nextCp.LastEtag;
                }
            }

            job.DocumentsFetched = fetchedDocs.Count;
            job.DocumentsPassedFilter = summary.PassedFilterCount;
            job.InsightsExtracted = summary.InsightsCreatedCount;
            job.CurrentStage = "Completed";
            job.ProgressPercent = 100;
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);

            Publish(job, $"Done: {summary.InsightsCreatedCount} new grounded insight(s) extracted ({summary.DeduplicatedSkipped} deduped, {summary.QuarantinedCount} quarantined).");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Ingestion job {JobId} failed", item.JobId);
            job.Status = JobStatus.Failed;
            job.CurrentStage = "Failed";
            job.ErrorMessage = ex.Message;
            job.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            Publish(job, $"Job failed: {ex.Message}");
        }
    }

    private void Publish(IngestionJob job, string message)
    {
        progressBus.Publish(new JobProgressEvent(
            job.Id,
            job.Status,
            job.CurrentStage,
            job.ProgressPercent,
            job.DocumentsFetched,
            job.DocumentsPassedFilter,
            job.InsightsExtracted,
            message,
            DateTimeOffset.UtcNow));
    }
}
