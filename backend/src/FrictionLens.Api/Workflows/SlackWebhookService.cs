using System.Net.Http.Json;
using FrictionLens.Api.Analytics;
using FrictionLens.Api.Data;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Ingestion;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Workflows;

/// <summary>
/// Formats and delivers Slack Block Kit Weekly Intelligence Digests & Spike Alerts
/// to configured workspace Incoming Webhooks (SSRF-guarded).
/// </summary>
public sealed class SlackWebhookService(
    AppDbContext db,
    IntelligenceService intelligence,
    IHttpClientFactory httpClientFactory)
{
    public async Task<(bool Sent, string Message)> SendWeeklyDigestAsync(Guid workspaceId, Guid? channelId, CancellationToken ct)
    {
        var query = db.NotificationChannels.Where(c => c.WorkspaceId == workspaceId && c.Enabled);
        if (channelId.HasValue) query = query.Where(c => c.Id == channelId.Value);

        var channels = await query.ToListAsync(ct);
        if (channels.Count == 0)
        {
            return (false, "No enabled Slack webhook channel configured for this workspace.");
        }

        var brief = await intelligence.GenerateWeeklyBriefAsync(workspaceId, ct);
        if (brief is null) return (false, "Workspace not found.");

        var payload = BuildBlockKitPayload(brief);
        var client = httpClientFactory.CreateClient("IngestionClient");
        var delivered = 0;

        foreach (var ch in channels)
        {
            var (allowed, reason, _) = await SsrfGuard.ValidateExternalUrlAsync(ch.WebhookUrl, ct);
            if (!allowed) return (false, $"Webhook URL blocked by SSRF guard: {reason}");

            using var resp = await client.PostAsJsonAsync(ch.WebhookUrl, payload, ct);
            if (resp.IsSuccessStatusCode)
            {
                ch.LastDeliveredAt = DateTimeOffset.UtcNow;
                delivered++;
            }
        }

        await db.SaveChangesAsync(ct);
        return delivered > 0
            ? (true, $"Delivered Slack Block Kit digest to {delivered} channel(s).")
            : (false, "Slack webhook delivery failed (check webhook URL).");
    }

    public static object BuildBlockKitPayload(WeeklyBriefDto brief)
    {
        var truncated = brief.MarkdownContent.Length > 2800
            ? brief.MarkdownContent[..2800] + "\n..."
            : brief.MarkdownContent;

        return new
        {
            text = $"FrictionLens Weekly Brief — {brief.WorkspaceName}",
            blocks = new object[]
            {
                new
                {
                    type = "header",
                    text = new { type = "plain_text", text = $"FrictionLens Brief — {brief.WorkspaceName}" }
                },
                new
                {
                    type = "section",
                    text = new { type = "mrkdwn", text = truncated }
                }
            }
        };
    }
}
