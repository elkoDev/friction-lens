using System.Net;
using System.Net.Http.Json;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FrictionLens.Tests.Integration;

public sealed class ApiIntegrationTests : IDisposable
{
    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"fl_test_{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ApiIntegrationTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Database:Provider"] = "Sqlite",
                        ["ConnectionStrings:Sqlite"] = $"Data Source={_tempDbPath}",
                        ["Extraction:Provider"] = "Heuristic"
                    });
                });
            });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task StatusAndSeededWorkspaces_ReturnValidResponses()
    {
        var status = await _client.GetFromJsonAsync<SystemStatusDto>("/api/status");
        Assert.NotNull(status);
        Assert.Equal("Sqlite", status.DatabaseProvider);
        Assert.Equal("Heuristic", status.ExtractionProvider);

        var workspaces = await _client.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        Assert.NotNull(workspaces);
        Assert.True(workspaces.Count >= 2);
    }

    [Fact]
    public async Task BulkCsvImport_RequiresOwnershipAttestation_AndExtractsGroundedInsightsWithPiiScrubbing()
    {
        var workspaces = await _client.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        var devTools = workspaces!.First(w => w.Slug.Contains("devtools", StringComparison.OrdinalIgnoreCase));

        // 1. Missing attestation must fail with 422
        var badResp = await _client.PostAsJsonAsync($"/api/workspaces/{devTools.Id}/ingest/csv",
            new BulkCsvIngestRequest("title,body\nT1,We switched from Firebase to Supabase because pricing cliff.", OwnershipConfirmed: false));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, badResp.StatusCode);

        // 2. Attested import with PII should scrub email and extract grounded insight
        var csv = "title,body,url\n" +
                  "\"Billing Switch\",\"Email me at ceo@startup.io — we switched from Firebase to Supabase because unpredictable pricing and vendor lock-in were total dealbreakers.\",https://example.com/takedown-target\n";

        var okResp = await _client.PostAsJsonAsync($"/api/workspaces/{devTools.Id}/ingest/csv",
            new BulkCsvIngestRequest(csv, OwnershipConfirmed: true, LegalBasis: "InternalFeedback", Format: "csv"));
        okResp.EnsureSuccessStatusCode();

        var summary = await okResp.Content.ReadFromJsonAsync<PipelineRunSummary>();
        Assert.NotNull(summary);
        Assert.Equal(1, summary.InsightsCreatedCount);

        var insights = await _client.GetFromJsonAsync<List<InsightDetailDto>>($"/api/workspaces/{devTools.Id}/insights?q=dealbreakers");
        Assert.NotNull(insights);
        Assert.Single(insights);
        Assert.DoesNotContain("ceo@startup.io", insights[0].VerbatimEvidence);
        Assert.True(insights[0].GroundingVerified);

        // 3. Takedown tombstone purges record and prevents re-ingestion
        var tdResp = await _client.PostAsJsonAsync("/api/compliance/takedowns",
            new TakedownRequest(devTools.Id, "https://example.com/takedown-target", null, null, TakedownReason.GdprArt17, "Tester"));
        tdResp.EnsureSuccessStatusCode();
        var td = await tdResp.Content.ReadFromJsonAsync<TakedownResponse>();
        Assert.NotNull(td);
        Assert.Equal(1, td.PurgedDocumentsCount);
        Assert.Equal(1, td.PurgedInsightsCount);

        // Re-ingesting the same URL must be tombstoned!
        var reIngestResp = await _client.PostAsJsonAsync($"/api/workspaces/{devTools.Id}/ingest/csv",
            new BulkCsvIngestRequest(csv, OwnershipConfirmed: true));
        var reSummary = await reIngestResp.Content.ReadFromJsonAsync<PipelineRunSummary>();
        Assert.NotNull(reSummary);
        Assert.Equal(1, reSummary.TombstonedSkipped);
        Assert.Equal(0, reSummary.InsightsCreatedCount);
    }

    [Fact]
    public async Task TeardownHtmlReport_AndSsrfProtectionOnSources_WorkEndToEnd()
    {
        var workspaces = await _client.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        var wsId = workspaces![0].Id;

        var reportHtml = await _client.GetStringAsync($"/api/workspaces/{wsId}/report/html");
        Assert.Contains("FrictionLens Executive Teardown Report", reportHtml);
        Assert.Contains("Head-to-Head Switching Matrix", reportHtml);

        // SSRF check: localhost Discourse source must be rejected with 400
        var ssrfResp = await _client.PostAsJsonAsync($"/api/workspaces/{wsId}/sources",
            new AddSourceRequest(SourceType.Discourse, "Evil Localhost", "http://127.0.0.1:8080"));
        Assert.Equal(HttpStatusCode.BadRequest, ssrfResp.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }
}
