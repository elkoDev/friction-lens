using System.Net;
using System.Net.Http.Json;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace FrictionLens.Tests.Integration;

public sealed class Phase2Tests : IDisposable
{
    private readonly string _tempDbPath = Path.Combine(Path.GetTempPath(), $"fl_p2_{Guid.NewGuid():N}.db");
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public Phase2Tests()
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
                        ["Extraction:Provider"] = "Heuristic",
                        ["Embedding:Provider"] = "LocalHash"
                    });
                });
            });

        _client = _factory.CreateClient();
    }

    [Fact]
    public async Task HealthProbes_AndDomainPacks_ReturnExpectedPayloads()
    {
        var live = await _client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);

        var ready = await _client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);

        var packs = await _client.GetFromJsonAsync<List<DomainPackDto>>("/api/domain-packs");
        Assert.NotNull(packs);
        Assert.Contains(packs, p => p.PackId == "devtools-cloud");
        Assert.Contains(packs, p => p.PackId == "prosumer-hardware");
    }

    [Fact]
    public async Task CreateWorkspaceWithDomainPack_SeedsCanonicalThemes_AndSupportsMergeAndRename()
    {
        var createResp = await _client.PostAsJsonAsync("/api/workspaces", new CreateWorkspaceRequest(
            "Cloud Observability",
            "Tracking Datadog vs Grafana friction",
            "DevTools",
            [new CreateEntityInput("Datadog", "dd,datadog", true), new CreateEntityInput("Grafana", "grafana,loki", false)],
            [],
            CompliancePolicy.StandardPublic,
            DomainPackId: "devtools-cloud"));

        Assert.Equal(HttpStatusCode.Created, createResp.StatusCode);
        var created = await createResp.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        var wsId = Guid.Parse(created!["id"].ToString()!);

        var themes = await _client.GetFromJsonAsync<List<CanonicalThemeDto>>($"/api/workspaces/{wsId}/themes");
        Assert.NotNull(themes);
        Assert.True(themes.Count >= 4);

        // Merge theme[1] into theme[0]
        var targetId = themes[0].Id;
        var sourceId = themes[1].Id;
        var mergeResp = await _client.PostAsJsonAsync($"/api/workspaces/{wsId}/themes/merge",
            new MergeThemesRequest([sourceId], targetId));
        mergeResp.EnsureSuccessStatusCode();

        var afterMerge = await _client.GetFromJsonAsync<List<CanonicalThemeDto>>($"/api/workspaces/{wsId}/themes");
        Assert.Equal(themes.Count - 1, afterMerge!.Count);

        // Rename surviving theme
        var renameResp = await _client.PatchAsJsonAsync($"/api/workspaces/{wsId}/themes/{targetId}",
            new RenameThemeRequest("Unified Pricing & Lock-In", DriverCategory.PricingTco));
        renameResp.EnsureSuccessStatusCode();

        var afterRename = await _client.GetFromJsonAsync<List<CanonicalThemeDto>>($"/api/workspaces/{wsId}/themes");
        Assert.Contains(afterRename!, t => t.Id == targetId && t.DisplayLabel == "Unified Pricing & Lock-In");
    }

    [Fact]
    public async Task Battlecard_AndTrendVelocity_WorkOnSeededWorkspace()
    {
        var workspaces = await _client.GetFromJsonAsync<List<WorkspaceSummaryDto>>("/api/workspaces");
        var devTools = workspaces!.First(w => w.Slug.Contains("devtools", StringComparison.OrdinalIgnoreCase));

        var cardResp = await _client.PostAsJsonAsync($"/api/workspaces/{devTools.Id}/exports/battlecard",
            new BattlecardRequest("Supabase", "Firebase"));
        cardResp.EnsureSuccessStatusCode();
        var card = await cardResp.Content.ReadFromJsonAsync<BattlecardDto>();

        Assert.NotNull(card);
        Assert.Contains("Sales Battlecard: Supabase vs. Firebase", card.MarkdownContent);
        Assert.NotEmpty(card.WhyWeWin);
        Assert.NotEmpty(card.LandminesToPlant);

        var trends = await _client.GetFromJsonAsync<List<TrendVelocityItemDto>>($"/api/workspaces/{devTools.Id}/trends");
        Assert.NotNull(trends);
        Assert.NotEmpty(trends);
        Assert.Equal(8, trends[0].WeeklySparkline.Count);
    }

    [Fact]
    public async Task HashEmbeddingProvider_ProducesHighCosineSimilarityForRelatedTerms()
    {
        var embedder = new HashEmbeddingProvider();
        var v1 = await embedder.EmbedAsync("remote programming tele-audiology support", CancellationToken.None);
        var v2 = await embedder.EmbedAsync("remote programming fitting support", CancellationToken.None);
        var v3 = await embedder.EmbedAsync("postgres serverless cold start latency", CancellationToken.None);

        var simRelated = VectorMath.CosineSimilarity(v1, v2);
        var simUnrelated = VectorMath.CosineSimilarity(v1, v3);

        Assert.True(simRelated > simUnrelated + 0.3f, $"Expected related ({simRelated:F2}) >> unrelated ({simUnrelated:F2})");
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }
}
