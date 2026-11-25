using System.Threading.RateLimiting;
using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Ingestion;

public sealed record RateLimitKey(SourceType SourceType, string Host);

/// <summary>
/// Per-(SourceType, Host) polite rate limiter using .NET built-in PartitionedRateLimiter.
/// Prevents hammering public APIs or starving other hosts during concurrent syncs.
/// </summary>
public sealed class ConnectorRateLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<RateLimitKey> _limiter = PartitionedRateLimiter.Create<RateLimitKey, string>(key =>
    {
        var partitionKey = $"{key.SourceType}:{key.Host.ToLowerInvariant()}";
        return key.SourceType switch
        {
            SourceType.HackerNews => RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromSeconds(2),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 20
            }),
            SourceType.GitHub => RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 8,
                TokensPerPeriod = 8,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }),
            _ => RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 3,
                TokensPerPeriod = 3,
                ReplenishmentPeriod = TimeSpan.FromSeconds(5),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            })
        };
    });

    public ValueTask<RateLimitLease> AcquireAsync(SourceType sourceType, string host, CancellationToken ct)
        => _limiter.AcquireAsync(new RateLimitKey(sourceType, host), 1, ct);

    public void Dispose() => _limiter.Dispose();
}
