using System.Net;
using System.Numerics;
using FrictionLens.Api.Domain;
using FrictionLens.Api.Extraction;
using FrictionLens.Api.Ingestion;
using Xunit;

namespace FrictionLens.Tests.Unit;

public sealed class PipelineUnitTests
{
    [Fact]
    public void PiiScrubber_RedactsEmailsPhonesHandlesAndSecrets_PreservesDatesAndVersions()
    {
        var input = "Contact alice.dev@example.com or +1 (415) 555-0199 or @alice_ops on 2026-11-24 about v11.0.100 using sk-1234567890abcdef12345.";
        var res = PiiScrubber.Scrub(input, CompliancePolicy.StandardPublic, SourceType.HackerNews);

        Assert.DoesNotContain("alice.dev@example.com", res.ScrubbedText);
        Assert.DoesNotContain("555-0199", res.ScrubbedText);
        Assert.DoesNotContain("@alice_ops", res.ScrubbedText);
        Assert.DoesNotContain("sk-1234567890", res.ScrubbedText);
        Assert.Contains("2026-11-24", res.ScrubbedText);
        Assert.Contains("11.0.100", res.ScrubbedText);
        Assert.True(res.RedactionsCount >= 4);
    }

    [Fact]
    public void PiiScrubber_BlocksPublicArt9HealthWithPersonalIdentifier()
    {
        var input = "Patient ID 99214 diagnosed with bilateral deafness, email john@clinic.org, comparing cochlear implants.";
        var res = PiiScrubber.Scrub(input, CompliancePolicy.StandardPublic, SourceType.HackerNews);

        Assert.True(res.ContainsArt9HealthTerms);
        Assert.True(res.BlockedByPolicy);
    }

    [Fact]
    public void GroundingVerifier_PassesExactAndEllipsis_RejectsNegationFlipAndHallucination()
    {
        var source = "Had the choice between MED-EL and AB. AB had the bluetooth and offered remote programming. If I went with Med-el it would be a 7h drive one way. MED-EL does not support remote programming yet.";

        var exact = GroundingVerifier.Evaluate(source, "AB had the bluetooth and offered remote programming.");
        Assert.True(exact.Verified);
        Assert.Equal("Exact", exact.Tier);

        var ellipsis = GroundingVerifier.Evaluate(source, "AB had the bluetooth... If I went with Med-el it would be a 7h drive one way");
        Assert.True(ellipsis.Verified);
        Assert.Equal("EllipsisOrdered", ellipsis.Tier);

        // Negation parity trap: dropping "not" must FAIL grounding!
        var flipped = GroundingVerifier.Evaluate(source, "MED-EL does support remote programming yet");
        Assert.False(flipped.Verified);

        // Hallucinated paraphrase must FAIL grounding!
        var hallucinated = GroundingVerifier.Evaluate(source, "Customer praised MED-EL for offering zero-cost overnight courier service.");
        Assert.False(hallucinated.Verified);
    }

    [Fact]
    public void LlmPromptBuilder_RejectsInvalidEnumValuesInsteadOfDefaultingToZero()
    {
        var badJson = """
            {
              "isActionable": true,
              "primaryEntity": "Supabase",
              "competitorsCompared": ["Firebase"],
              "outcome": "TotallyInvalidEnumValue",
              "summary": "Test",
              "unmetRequirement": "Test",
              "verbatimEvidence": "We switched from Firebase to Supabase",
              "confidence": 0.9,
              "drivers": []
            }
            """;

        var parsed = LlmPromptBuilder.ParseResponse(badJson, "test-model", 100, 50);
        Assert.Null(parsed);
    }

    [Fact]
    public void SignalFilter_RejectsShortAliasFalsePositivesAndHiringPosts()
    {
        var entities = new List<TrackedEntity>
        {
            new() { Name = "Advanced Bionics", AliasesCsv = "AB" },
            new() { Name = "MED-EL", AliasesCsv = "Med-el" }
        };

        var abTestNoise = SignalFilter.Evaluate("We ran an a/b test on our landing page conversion funnel yesterday.", entities, SourceType.HackerNews);
        Assert.False(abTestNoise.Passed);

        var devEntities = new List<TrackedEntity> { new() { Name = "Supabase", AliasesCsv = "supabase" } };
        var hiringNoise = SignalFilter.Evaluate("Who is hiring: Senior Backend Engineer working with Supabase and Postgres, apply here!", devEntities, SourceType.HackerNews);
        Assert.False(hiringNoise.Passed);
    }

    [Fact]
    public void CsvJsonlImportService_ParsesQuotedMultilineCsvAndJsonl()
    {
        var csv = "title,body,url\n\"Row 1\",\"We switched from Firebase to Supabase,\nbecause vendor lock-in was a dealbreaker.\",https://example.com/1\n";
        var csvRes = CsvJsonlImportService.Parse(csv, "csv");
        Assert.Single(csvRes.Documents);
        Assert.Contains("vendor lock-in", csvRes.Documents[0].Body);

        var jsonl = "{\"title\":\"T1\",\"text\":\"Switched from Firebase to Supabase due to pricing cliff.\"}\n{bad json}\n";
        var jsonlRes = CsvJsonlImportService.Parse(jsonl, "jsonl");
        Assert.Single(jsonlRes.Documents);
        Assert.Equal(1, jsonlRes.MalformedRows);
    }

    [Fact]
    public void SimHash64_DetectsNearDuplicatesWithSmallHammingDistance()
    {
        var a = "We switched from Firebase to Supabase mainly because Firestore vendor lock-in made SQL reporting painful and we wanted self-hosting.";
        var b = "We switched from Firebase to Supabase mainly because Firestore vendor lock-in made SQL reporting painful and we needed self-hosting.";
        var c = "Had the choice between MED-EL and AB. AB had bluetooth and remote programming while MED-EL required a 7h drive.";

        var ha = ExtractionPipeline.ComputeSimHash64(a);
        var hb = ExtractionPipeline.ComputeSimHash64(b);
        var hc = ExtractionPipeline.ComputeSimHash64(c);

        var nearDist = BitOperations.PopCount((ulong)(ha ^ hb));
        var farDist = BitOperations.PopCount((ulong)(ha ^ hc));

        Assert.True(nearDist <= 12, $"Expected near-duplicate distance <= 12, got {nearDist}");
        Assert.True(farDist > nearDist + 8, $"Expected unrelated distance ({farDist}) >> near distance ({nearDist})");
    }

    [Fact]
    public void SsrfGuard_BlocksPrivateAndLoopbackIps()
    {
        Assert.True(SsrfGuard.IsPrivateOrReserved(IPAddress.Parse("127.0.0.1")));
        Assert.True(SsrfGuard.IsPrivateOrReserved(IPAddress.Parse("169.254.169.254")));
        Assert.True(SsrfGuard.IsPrivateOrReserved(IPAddress.Parse("10.0.0.5")));
        Assert.True(SsrfGuard.IsPrivateOrReserved(IPAddress.Parse("192.168.1.10")));
        Assert.False(SsrfGuard.IsPrivateOrReserved(IPAddress.Parse("1.1.1.1")));
    }

    [Fact]
    public void CanonicalTaxonomyResolver_CollapsesKnownSynonyms()
    {
        var s1 = CanonicalTaxonomyResolver.ResolveCanonicalSlugOffline("tele_audiology", "Tele-Audiology", "offered tele-audiology");
        var s2 = CanonicalTaxonomyResolver.ResolveCanonicalSlugOffline("remote_fitting", "Remote Fitting", "supports remote fitting");
        var s3 = CanonicalTaxonomyResolver.ResolveCanonicalSlugOffline("billing_shock", "Billing Shock", "unexpected bill shock");

        Assert.Equal("remote_programming", s1);
        Assert.Equal("remote_programming", s2);
        Assert.Equal("pricing_predictability", s3);
    }
}
