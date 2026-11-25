using System.Text;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Analytics;

/// <summary>
/// Synthesizes a strictly grounded Sales Battlecard (Why We Win, Known Gaps & Objections,
/// Landmines to Plant, and Verified Citation Appendix) for PrimaryEntity vs. CompetitorEntity.
/// </summary>
public sealed class BattlecardBuilder(AppDbContext db)
{
    public async Task<BattlecardDto?> BuildAsync(Guid workspaceId, string primaryEntity, string competitorEntity, CancellationToken ct)
    {
        var workspace = await db.Workspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

        if (workspace is null) return null;

        var insights = (await db.DecisionInsights
            .AsNoTracking()
            .Include(i => i.Drivers)
            .Where(i => i.WorkspaceId == workspaceId && i.GroundingVerified && !i.IsQuarantined)
            .ToListAsync(ct))
            .Where(i =>
                MentionsEntity(i, primaryEntity) || MentionsEntity(i, competitorEntity))
            .OrderByDescending(i => i.ObservedAt)
            .ToList();

        var headToHead = insights
            .Where(i => MentionsEntity(i, primaryEntity) && MentionsEntity(i, competitorEntity))
            .ToList();

        var allDrivers = insights.SelectMany(i => i.Drivers.Select(d => (Driver: d, Insight: i))).ToList();

        var whyWeWin = allDrivers
            .Where(x => x.Driver.TargetEntity.Equals(primaryEntity, StringComparison.OrdinalIgnoreCase)
                && x.Driver.Polarity == DriverPolarity.Advantage)
            .GroupBy(x => x.Driver.DisplayLabel)
            .OrderByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key} ({g.Count()}x) — \"{Truncate(g.First().Insight.VerbatimEvidence, 140)}\"")
            .ToList();

        var knownGaps = allDrivers
            .Where(x => x.Driver.TargetEntity.Equals(primaryEntity, StringComparison.OrdinalIgnoreCase)
                && x.Driver.Polarity == DriverPolarity.Drawback)
            .GroupBy(x => x.Driver.DisplayLabel)
            .OrderByDescending(g => g.Count(x => x.Driver.Severity == DriverSeverity.DealBreaker))
            .ThenByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"{g.Key} ({g.Count()}x, {g.Count(x => x.Driver.Severity == DriverSeverity.DealBreaker)} deal-breakers) — Unmet need: {g.First().Insight.UnmetRequirement}")
            .ToList();

        var landmines = allDrivers
            .Where(x => x.Driver.TargetEntity.Equals(competitorEntity, StringComparison.OrdinalIgnoreCase)
                && x.Driver.Polarity == DriverPolarity.Drawback)
            .GroupBy(x => x.Driver.DisplayLabel)
            .OrderByDescending(g => g.Count(x => x.Driver.Severity == DriverSeverity.DealBreaker))
            .ThenByDescending(g => g.Count())
            .Take(5)
            .Select(g => $"Ask prospects how {competitorEntity} handles **{g.Key}** ({g.Count()}x cited friction) — Proof: \"{Truncate(g.First().Insight.VerbatimEvidence, 130)}\"")
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Sales Battlecard: {primaryEntity} vs. {competitorEntity}");
        sb.AppendLine($"*Workspace: {workspace.Name} | Generated: {DateTimeOffset.UtcNow:yyyy-MM-dd} UTC | Head-to-Head Signals: {headToHead.Count} | Total Verified Citations: {insights.Count}*");
        sb.AppendLine();

        sb.AppendLine($"## 1. Why Buyers Choose {primaryEntity} (Verified Advantages)");
        if (whyWeWin.Count == 0) sb.AppendLine("- No verified advantage signals captured yet.");
        else foreach (var item in whyWeWin) sb.AppendLine($"- {item}");

        sb.AppendLine();
        sb.AppendLine($"## 2. Known Gaps & Objections Against {primaryEntity}");
        if (knownGaps.Count == 0) sb.AppendLine("- No verified friction signals captured against this entity yet.");
        else foreach (var item in knownGaps) sb.AppendLine($"- {item}");

        sb.AppendLine();
        sb.AppendLine($"## 3. Landmines to Plant Against {competitorEntity}");
        if (landmines.Count == 0) sb.AppendLine($"- No verified drawback signals captured for {competitorEntity} yet.");
        else foreach (var item in landmines) sb.AppendLine($"- {item}");

        sb.AppendLine();
        sb.AppendLine("## 4. Grounded Citation Appendix");
        foreach (var ins in insights.Take(8))
        {
            var link = string.IsNullOrWhiteSpace(ins.SourceUrl) ? ins.SourceType.ToString() : $"[{ins.SourceType}]({ins.SourceUrl})";
            sb.AppendLine($"- **{ins.PrimaryEntity}** ({ins.Outcome}, {link}): \"{ins.VerbatimEvidence}\"");
        }

        return new BattlecardDto(
            workspaceId,
            primaryEntity,
            competitorEntity,
            headToHead.Count,
            whyWeWin,
            knownGaps,
            landmines,
            sb.ToString(),
            DateTimeOffset.UtcNow);
    }

    private static bool MentionsEntity(DecisionInsight i, string entity)
        => i.PrimaryEntity.Equals(entity, StringComparison.OrdinalIgnoreCase)
           || i.CompetitorsComparedCsv.Contains(entity, StringComparison.OrdinalIgnoreCase);

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max].TrimEnd() + "...";
}
