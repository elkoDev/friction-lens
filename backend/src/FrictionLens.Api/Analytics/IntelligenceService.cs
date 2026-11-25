using System.Text;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Analytics;

public sealed class IntelligenceService(AppDbContext db)
{
    public async Task<WinLossMatrixDto?> BuildWinLossMatrixAsync(
        Guid workspaceId,
        CancellationToken ct,
        bool includeQuarantined = false)
    {
        var workspace = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Entities)
            .Include(w => w.Insights)
                .ThenInclude(i => i.Drivers)
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

        if (workspace is null) return null;

        var eligibleInsights = includeQuarantined
            ? workspace.Insights
            : workspace.Insights.Where(i => i.GroundingVerified && !i.IsQuarantined).ToList();

        var pairStats = new Dictionary<(string Winner, string Loser), (int Count, List<string> WinDrivers, List<string> LossFrictions)>();

        foreach (var insight in eligibleInsights)
        {
            var rivals = insight.CompetitorsComparedCsv
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (rivals.Count == 0) continue;

            foreach (var rival in rivals)
            {
                string winner;
                string loser;

                if (insight.Outcome is DecisionOutcome.RejectedPrimary or DecisionOutcome.SwitchedAway)
                {
                    winner = rival;
                    loser = insight.PrimaryEntity;
                }
                else
                {
                    winner = insight.PrimaryEntity;
                    loser = rival;
                }

                var key = (winner, loser);
                if (!pairStats.TryGetValue(key, out var entry))
                {
                    entry = (0, [], []);
                }

                var winDrivers = insight.Drivers
                    .Where(d => d.Polarity == DriverPolarity.Advantage)
                    .Select(d => d.DisplayLabel);

                var lossFrictions = insight.Drivers
                    .Where(d => d.Polarity == DriverPolarity.Drawback)
                    .Select(d => d.DisplayLabel);

                entry.Count++;
                entry.WinDrivers.AddRange(winDrivers);
                entry.LossFrictions.AddRange(lossFrictions);
                pairStats[key] = entry;
            }
        }

        var cells = pairStats
            .Select(kvp => new HeadToHeadCellDto(
                kvp.Key.Winner,
                kvp.Key.Loser,
                kvp.Value.Count,
                kvp.Value.WinDrivers.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList(),
                kvp.Value.LossFrictions.GroupBy(x => x).OrderByDescending(g => g.Count()).Select(g => g.Key).Take(3).ToList()))
            .OrderByDescending(c => c.WinCount)
            .ToList();

        return new WinLossMatrixDto(
            workspace.Id,
            workspace.Name,
            workspace.Insights.Count,
            workspace.Insights.Count(i => i.GroundingVerified && !i.IsQuarantined),
            workspace.Entities.Select(e => e.Name).ToList(),
            cells);
    }

    public async Task<IReadOnlyList<FrictionRankingItemDto>> GetFrictionRankingAsync(
        Guid workspaceId,
        DriverPolarity? polarityFilter,
        CancellationToken ct,
        bool includeQuarantined = false)
    {
        var query = db.FrictionDrivers
            .AsNoTracking()
            .Include(d => d.DecisionInsight)
            .Where(d => d.DecisionInsight != null && d.DecisionInsight.WorkspaceId == workspaceId);

        if (!includeQuarantined)
        {
            query = query.Where(d => d.DecisionInsight!.GroundingVerified && !d.DecisionInsight.IsQuarantined);
        }

        if (polarityFilter.HasValue)
        {
            query = query.Where(d => d.Polarity == polarityFilter.Value);
        }

        var drivers = await query.ToListAsync(ct);

        return drivers
            .GroupBy(d => new { d.CanonicalTheme, d.TargetEntity, d.Polarity })
            .Select(g =>
            {
                var first = g.First();
                return new FrictionRankingItemDto(
                    g.Key.CanonicalTheme,
                    first.DisplayLabel,
                    first.Category,
                    g.Key.TargetEntity,
                    g.Key.Polarity,
                    g.Count(),
                    g.Count(x => x.Severity == DriverSeverity.DealBreaker),
                    g.Select(x => x.DecisionInsight!.VerbatimEvidence).Distinct().Take(2).ToList());
            })
            .OrderByDescending(x => x.DealBreakerCount)
            .ThenByDescending(x => x.MentionCount)
            .ToList();
    }

    public async Task<WeeklyBriefDto?> GenerateWeeklyBriefAsync(Guid workspaceId, CancellationToken ct)
    {
        var matrix = await BuildWinLossMatrixAsync(workspaceId, ct);
        if (matrix is null) return null;

        var drawbacks = await GetFrictionRankingAsync(workspaceId, DriverPolarity.Drawback, ct);
        var advantages = await GetFrictionRankingAsync(workspaceId, DriverPolarity.Advantage, ct);
        var recentInsights = (await db.DecisionInsights
            .AsNoTracking()
            .Where(i => i.WorkspaceId == workspaceId && i.GroundingVerified && !i.IsQuarantined)
            .ToListAsync(ct))
            .OrderByDescending(i => i.ObservedAt)
            .Take(5)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Weekly Decision & Friction Brief — {matrix.WorkspaceName}");
        sb.AppendLine($"*Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC | Grounded Insights: {matrix.GroundedInsights}/{matrix.TotalInsights}*");
        sb.AppendLine();

        sb.AppendLine("## 1. Head-to-Head Switching Signals");
        if (matrix.Comparisons.Count == 0)
        {
            sb.AppendLine("- No head-to-head comparisons captured yet.");
        }
        else
        {
            foreach (var c in matrix.Comparisons.Take(5))
            {
                var winStr = c.TopWinningDrivers.Count > 0 ? string.Join(", ", c.TopWinningDrivers) : "General preference";
                var lossStr = c.TopLosingFrictions.Count > 0 ? string.Join(", ", c.TopLosingFrictions) : "Unspecified friction";
                sb.AppendLine($"- **{c.WinnerEntity} over {c.LoserEntity}** ({c.WinCount} signals) — Won on: *{winStr}* | Rival friction: *{lossStr}*");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 2. Top Deal-Breaker Friction Points");
        foreach (var d in drawbacks.Take(5))
        {
            sb.AppendLine($"- **{d.DisplayLabel}** (`{d.TargetEntity}`, {d.Category}) — {d.MentionCount} mentions ({d.DealBreakerCount} deal-breakers)");
        }

        sb.AppendLine();
        sb.AppendLine("## 3. Emerging Competitive Advantages");
        foreach (var a in advantages.Take(4))
        {
            sb.AppendLine($"- **{a.DisplayLabel}** (`{a.TargetEntity}`) — {a.MentionCount} mentions");
        }

        sb.AppendLine();
        sb.AppendLine("## 4. Verifiable Verbatim Evidence & Unmet Requirements");
        foreach (var ins in recentInsights)
        {
            sb.AppendLine($"- **[{ins.PrimaryEntity} · {ins.Outcome}]** \"{ins.VerbatimEvidence}\"");
            if (!string.IsNullOrWhiteSpace(ins.UnmetRequirement))
            {
                sb.AppendLine($"  - *Unmet Requirement:* {ins.UnmetRequirement}");
            }
        }

        return new WeeklyBriefDto(
            workspaceId,
            matrix.WorkspaceName,
            DateTimeOffset.UtcNow,
            matrix.GroundedInsights,
            sb.ToString());
    }
}
