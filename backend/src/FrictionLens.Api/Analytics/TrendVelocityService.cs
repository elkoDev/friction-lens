using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Analytics;

/// <summary>
/// Computes Laplace-smoothed WoW trend velocity, 8-week sparkline buckets,
/// and spike detection per canonical theme + entity.
/// </summary>
public sealed class TrendVelocityService(AppDbContext db)
{
    public async Task<IReadOnlyList<TrendVelocityItemDto>> ComputeVelocityAsync(Guid workspaceId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var windowStart = now.AddDays(-56); // 8 weeks

        var drivers = await db.FrictionDrivers
            .AsNoTracking()
            .Include(d => d.DecisionInsight)
            .Where(d => d.DecisionInsight != null
                && d.DecisionInsight.WorkspaceId == workspaceId
                && d.DecisionInsight.GroundingVerified
                && !d.DecisionInsight.IsQuarantined)
            .ToListAsync(ct);

        var recent = drivers.Where(d => d.DecisionInsight!.ObservedAt >= windowStart).ToList();

        return recent
            .GroupBy(d => new { d.CanonicalTheme, d.TargetEntity })
            .Select(g =>
            {
                var first = g.First();
                var buckets = new int[8];
                foreach (var item in g)
                {
                    var daysAgo = Math.Max(0, (now - item.DecisionInsight!.ObservedAt).TotalDays);
                    var weekIdx = Math.Clamp(7 - (int)(daysAgo / 7.0), 0, 7);
                    buckets[weekIdx]++;
                }

                var curr = buckets[7];
                var prev = buckets[6];
                // Laplace-smoothed WoW velocity (alpha = 1.0)
                var velocityPct = Math.Round((((curr + 1.0) / (prev + 1.0)) - 1.0) * 100.0, 1);

                var baselineAvg = buckets.Take(7).Average();
                var isSpike = curr >= 3 && (baselineAvg == 0 || curr >= baselineAvg * 2.5);

                return new TrendVelocityItemDto(
                    g.Key.CanonicalTheme,
                    first.DisplayLabel,
                    g.Key.TargetEntity,
                    curr,
                    prev,
                    velocityPct,
                    isSpike,
                    buckets);
            })
            .OrderByDescending(x => x.IsSpike)
            .ThenByDescending(x => x.CurrentWindowCount)
            .ThenByDescending(x => x.VelocityPct)
            .Take(10)
            .ToList();
    }
}
