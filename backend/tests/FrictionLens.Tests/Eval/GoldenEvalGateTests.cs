using System.Text.Json;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using Xunit;
using Xunit.Abstractions;

namespace FrictionLens.Tests.Eval;

public sealed class GoldenEvalGateTests(ITestOutputHelper output)
{
    private sealed record GoldenEntity(string Name, string Aliases);
    private sealed record GoldenWorkspace(string DomainCategory, List<GoldenEntity> Entities);
    private sealed record GoldenCase(
        string Id,
        string Workspace,
        string SourceType,
        string Bucket,
        string Title,
        string Body,
        bool ExpectedPassPipeline,
        string? ExpectedPrimaryEntity,
        string? ExpectedCanonicalTheme);
    private sealed record GoldenDataset(Dictionary<string, GoldenWorkspace> Workspaces, List<GoldenCase> Cases);

    [Fact]
    public async Task GoldenEvalGate_MeetsPrecisionRecallGroundingAndSynonymThresholds()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Eval", "golden_dataset.json");
        var json = await File.ReadAllTextAsync(path);
        var dataset = JsonSerializer.Deserialize<GoldenDataset>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        var extractor = new HeuristicExtractionProvider();

        int posTotal = 0, posPassed = 0;
        int negTotal = 0, negRejected = 0;
        int entityCorrect = 0, themeCorrect = 0, groundedPass = 0, extractedCount = 0;

        foreach (var c in dataset.Cases)
        {
            var ws = dataset.Workspaces[c.Workspace];
            var entities = ws.Entities.Select(e => new TrackedEntity { Name = e.Name, AliasesCsv = e.Aliases }).ToList();
            var sourceType = Enum.Parse<SourceType>(c.SourceType, ignoreCase: true);

            var combined = $"{c.Title} {c.Body}";
            var s1 = SignalFilter.Evaluate(combined, entities, sourceType);
            var s2 = s1.Passed ? TriageScorer.Score(combined, s1.MatchedEntities, sourceType) : null;
            var passedPipeline = s1.Passed && (s2?.IsActionable == true);

            if (c.ExpectedPassPipeline)
            {
                posTotal++;
                if (passedPipeline)
                {
                    posPassed++;
                    var ctx = new ExtractionContext(ws.DomainCategory, entities, [], c.Title, c.Body);
                    var res = await extractor.ExtractAsync(ctx, CancellationToken.None);
                    Assert.NotNull(res);
                    extractedCount++;

                    if (string.Equals(res.PrimaryEntity, c.ExpectedPrimaryEntity, StringComparison.OrdinalIgnoreCase))
                        entityCorrect++;

                    var grounding = GroundingVerifier.Evaluate(combined, res.VerbatimEvidence);
                    if (grounding.Verified) groundedPass++;

                    var resolvedThemes = res.Drivers
                        .Select(d => CanonicalTaxonomyResolver.ResolveCanonicalSlugOffline(d.CanonicalTheme, d.DisplayLabel, d.RawPhrase))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    if (c.ExpectedCanonicalTheme is not null && resolvedThemes.Contains(c.ExpectedCanonicalTheme))
                        themeCorrect++;
                }
            }
            else
            {
                negTotal++;
                if (!passedPipeline) negRejected++;
            }
        }

        var posRecall = (double)posPassed / posTotal;
        var negRejection = (double)negRejected / negTotal;
        var entityAccuracy = (double)entityCorrect / Math.Max(extractedCount, 1);
        var themeAccuracy = (double)themeCorrect / Math.Max(extractedCount, 1);
        var groundingRate = (double)groundedPass / Math.Max(extractedCount, 1);

        output.WriteLine("=== FRICTIONLENS GOLDEN EVAL SCORECARD ===");
        output.WriteLine($"Positive Pipeline Recall : {posRecall:P1} ({posPassed}/{posTotal}) [Gate >= 90%]");
        output.WriteLine($"Hard-Negative Rejection  : {negRejection:P1} ({negRejected}/{negTotal}) [Gate >= 85%]");
        output.WriteLine($"Primary Entity Accuracy  : {entityAccuracy:P1} ({entityCorrect}/{extractedCount}) [Gate >= 90%]");
        output.WriteLine($"Canonical Theme Accuracy : {themeAccuracy:P1} ({themeCorrect}/{extractedCount}) [Gate >= 90%]");
        output.WriteLine($"Verbatim Grounding Rate  : {groundingRate:P1} ({groundedPass}/{extractedCount}) [Gate == 100%]");

        Assert.True(posRecall >= 0.90, $"Positive recall {posRecall:P1} below 90% gate.");
        Assert.True(negRejection >= 0.85, $"Hard-negative rejection {negRejection:P1} below 85% gate.");
        Assert.True(entityAccuracy >= 0.90, $"Entity attribution accuracy {entityAccuracy:P1} below 90% gate.");
        Assert.True(themeAccuracy >= 0.90, $"Canonical theme accuracy {themeAccuracy:P1} below 90% gate.");
        Assert.Equal(1.0, groundingRate);
    }
}
