using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

public sealed record FetchedDocument(
    SourceType SourceType,
    string ExternalId,
    string Title,
    string Body,
    string SourceUrl,
    DateTimeOffset PublishedAt);

public sealed record SyncCheckpoint(
    DateTimeOffset? CursorTimestamp,
    string? LastExternalId,
    string? CursorToken,
    string? LastEtag);

public sealed record ConnectorFetchContext(
    SourceConfig Source,
    IReadOnlyList<TrackedEntity> Entities,
    int MaxItems,
    SyncCheckpoint? Checkpoint);

public sealed record ConnectorFetchResult(
    IReadOnlyList<FetchedDocument> Documents,
    SyncCheckpoint NextCheckpoint,
    bool NotModified = false,
    string? Warning = null);

public interface ISourceConnector
{
    SourceType SupportedSource { get; }
    Task<ConnectorFetchResult> FetchAsync(ConnectorFetchContext context, CancellationToken ct);
}
