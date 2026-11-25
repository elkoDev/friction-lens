using System.Net;
using System.Text;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Analytics;

/// <summary>
/// Generates a standalone, print-to-PDF-ready HTML "Trojan Horse" Executive Teardown Report
/// for any workspace (Win/Loss Matrix, Deal-Breaker Rankings, Unmet Requirements, and Verbatim Appendix).
/// </summary>
public sealed class TeardownReportBuilder(AppDbContext db, IntelligenceService intelligence)
{
    public async Task<string?> BuildHtmlReportAsync(Guid workspaceId, CancellationToken ct)
    {
        var matrix = await intelligence.BuildWinLossMatrixAsync(workspaceId, ct);
        if (matrix is null) return null;

        var workspace = await db.Workspaces
            .AsNoTracking()
            .Include(w => w.Entities)
            .FirstAsync(w => w.Id == workspaceId, ct);

        var drawbacks = await intelligence.GetFrictionRankingAsync(workspaceId, DriverPolarity.Drawback, ct);
        var advantages = await intelligence.GetFrictionRankingAsync(workspaceId, DriverPolarity.Advantage, ct);

        var insights = (await db.DecisionInsights
            .AsNoTracking()
            .Include(i => i.Drivers)
            .Where(i => i.WorkspaceId == workspaceId && i.GroundingVerified && !i.IsQuarantined)
            .ToListAsync(ct))
            .OrderByDescending(i => i.ObservedAt)
            .Take(25)
            .ToList();

        var groundingPct = matrix.TotalInsights > 0
            ? (int)Math.Round(100.0 * matrix.GroundedInsights / matrix.TotalInsights)
            : 100;

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"UTF-8\"/>");
        sb.AppendLine($"<title>Decision &amp; Friction Teardown — {Enc(workspace.Name)}</title>");
        sb.AppendLine("""
            <style>
              @page { size: letter; margin: 16mm 14mm; }
              * { box-sizing: border-box; }
              body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; color: #17191e; background: #fff; margin: 0; padding: 32px; line-height: 1.5; }
              .container { max-width: 860px; margin: 0 auto; }
              header { border-bottom: 2px solid #17191e; padding-bottom: 16px; margin-bottom: 24px; }
              .eyebrow { font-size: 11px; text-transform: uppercase; letter-spacing: 0.1em; color: #0f766e; font-weight: 700; }
              h1 { margin: 6px 0 8px; font-size: 26px; letter-spacing: -0.02em; }
              .meta { font-size: 13px; color: #535966; }
              .kpis { display: grid; grid-template-columns: repeat(3, 1fr); gap: 12px; margin: 20px 0 28px; }
              .kpi { border: 1px solid #e4e1d8; border-radius: 8px; padding: 14px; background: #f9f8f5; }
              .kpi-label { font-size: 11px; text-transform: uppercase; color: #7c828f; font-weight: 600; }
              .kpi-val { font-size: 24px; font-weight: 700; margin-top: 4px; font-family: ui-monospace, monospace; }
              h2 { font-size: 16px; border-bottom: 1px solid #e4e1d8; padding-bottom: 6px; margin: 26px 0 12px; break-after: avoid; }
              table { width: 100%; border-collapse: collapse; font-size: 13px; margin-bottom: 16px; }
              th, td { text-align: left; padding: 9px 10px; border-bottom: 1px solid #e4e1d8; vertical-align: top; }
              th { background: #f6f5f1; font-size: 11px; text-transform: uppercase; letter-spacing: 0.05em; color: #535966; }
              .win { color: #0f766e; font-weight: 700; }
              .loss { color: #b93822; font-weight: 700; }
              .badge { display: inline-block; padding: 2px 7px; border-radius: 999px; font-size: 11px; font-weight: 600; }
              .b-red { background: #fcece9; color: #b93822; }
              .b-green { background: #e6f4f1; color: #0f766e; }
              .quote-card { border: 1px solid #e4e1d8; border-radius: 8px; padding: 14px; margin-bottom: 12px; break-inside: avoid; }
              blockquote { margin: 8px 0; padding: 8px 12px; border-left: 3px solid #17191e; background: #f6f5f1; font-size: 13.5px; }
              .req { font-size: 12.5px; color: #6b3f06; background: #fdf3e3; padding: 6px 10px; border-radius: 6px; margin-top: 8px; }
              footer { margin-top: 32px; padding-top: 12px; border-top: 1px solid #e4e1d8; font-size: 11.5px; color: #7c828f; display: flex; justify-content: space-between; }
              @media print { body { padding: 0; } .no-print { display: none; } }
            </style></head><body><div class="container">
            """);

        sb.AppendLine("<header>");
        sb.AppendLine("<div class=\"eyebrow\">FrictionLens Executive Teardown Report</div>");
        sb.AppendLine($"<h1>{Enc(workspace.Name)}</h1>");
        sb.AppendLine($"<div class=\"meta\">{Enc(workspace.Description)}<br/>Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC · Tracked Entities: {Enc(string.Join(", ", matrix.Entities))}</div>");
        sb.AppendLine("</header>");

        sb.AppendLine("<div class=\"kpis\">");
        sb.AppendLine($"<div class=\"kpi\"><div class=\"kpi-label\">Verified Decisions</div><div class=\"kpi-val\">{matrix.GroundedInsights}</div></div>");
        sb.AppendLine($"<div class=\"kpi\"><div class=\"kpi-label\">Quote Grounding</div><div class=\"kpi-val\">{groundingPct}%</div></div>");
        sb.AppendLine($"<div class=\"kpi\"><div class=\"kpi-label\">Deal-Breaker Themes</div><div class=\"kpi-val\">{drawbacks.Count(d => d.DealBreakerCount > 0)}</div></div>");
        sb.AppendLine("</div>");

        sb.AppendLine("<h2>1. Head-to-Head Switching Matrix</h2>");
        if (matrix.Comparisons.Count == 0)
        {
            sb.AppendLine("<p class=\"meta\">No head-to-head comparisons captured yet.</p>");
        }
        else
        {
            sb.AppendLine("<table><thead><tr><th>Winner</th><th>Loser</th><th>Signals</th><th>Winning Drivers</th><th>Rival Friction</th></tr></thead><tbody>");
            foreach (var c in matrix.Comparisons)
            {
                sb.AppendLine($"<tr><td class=\"win\">{Enc(c.WinnerEntity)}</td><td class=\"loss\">{Enc(c.LoserEntity)}</td><td>{c.WinCount}</td><td>{Enc(string.Join(", ", c.TopWinningDrivers))}</td><td>{Enc(string.Join(", ", c.TopLosingFrictions))}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("<h2>2. Top Friction &amp; Deal-Breaker Drivers</h2>");
        sb.AppendLine("<table><thead><tr><th>Canonical Theme</th><th>Entity</th><th>Category</th><th>Mentions</th><th>Deal-Breakers</th></tr></thead><tbody>");
        foreach (var d in drawbacks.Take(10))
        {
            sb.AppendLine($"<tr><td><strong>{Enc(d.DisplayLabel)}</strong><br/><span class=\"meta\">{Enc(d.CanonicalTheme)}</span></td><td>{Enc(d.TargetEntity)}</td><td>{d.Category}</td><td>{d.MentionCount}</td><td><span class=\"badge b-red\">{d.DealBreakerCount}</span></td></tr>");
        }
        sb.AppendLine("</tbody></table>");

        if (advantages.Count > 0)
        {
            sb.AppendLine("<h2>3. Verified Competitive Strengths</h2>");
            sb.AppendLine("<table><thead><tr><th>Canonical Theme</th><th>Entity</th><th>Category</th><th>Mentions</th></tr></thead><tbody>");
            foreach (var a in advantages.Take(8))
            {
                sb.AppendLine($"<tr><td><strong>{Enc(a.DisplayLabel)}</strong></td><td>{Enc(a.TargetEntity)}</td><td>{a.Category}</td><td><span class=\"badge b-green\">{a.MentionCount}</span></td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        sb.AppendLine("<h2>4. Verifiable Verbatim Evidence &amp; Unmet Requirements</h2>");
        foreach (var ins in insights)
        {
            var rival = string.IsNullOrWhiteSpace(ins.CompetitorsComparedCsv) ? "" : $" vs. {ins.CompetitorsComparedCsv}";
            sb.AppendLine("<div class=\"quote-card\">");
            sb.AppendLine($"<div class=\"meta\"><strong>{Enc(ins.PrimaryEntity)}{Enc(rival)}</strong> · {ins.Outcome} · {ins.SourceType} · Grounded ({ins.GroundingTier})</div>");
            sb.AppendLine($"<blockquote>“{Enc(ins.VerbatimEvidence)}”</blockquote>");
            if (!string.IsNullOrWhiteSpace(ins.UnmetRequirement))
            {
                sb.AppendLine($"<div class=\"req\"><strong>Unmet Requirement:</strong> {Enc(ins.UnmetRequirement)}</div>");
            }
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<footer><span>Generated by FrictionLens Decision &amp; Friction Intelligence</span><span>100% Deterministically Grounded Citations</span></footer>");
        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
