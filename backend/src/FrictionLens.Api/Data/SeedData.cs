using FrictionLens.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace FrictionLens.Api.Data;

public static class SeedData
{
    public static async Task EnsureSeededAsync(AppDbContext db)
    {
        if (await db.Workspaces.AnyAsync())
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;

        // Starter Workspace 1: B2B DevTools (Live compliant HN + GitHub connectors ready to run)
        var devTools = new Workspace
        {
            Name = "B2B DevTools — Postgres & BaaS",
            Slug = "b2b-devtools-baas",
            Description = "Tracks developer switching drivers, pricing friction, and unmet requirements across Supabase, Firebase, and Neon.",
            DomainCategory = "B2B Developer Tools & Cloud Infrastructure",
            Entities =
            [
                new TrackedEntity { Name = "Supabase", AliasesCsv = "supabase", IsPrimaryBrand = true },
                new TrackedEntity { Name = "Firebase", AliasesCsv = "firestore,firebase", IsPrimaryBrand = false },
                new TrackedEntity { Name = "Neon", AliasesCsv = "neon.tech,neondb", IsPrimaryBrand = false }
            ],
            Sources =
            [
                new SourceConfig { SourceType = SourceType.HackerNews, Label = "HN: Supabase vs Firebase", Target = "Supabase Firebase", Enabled = true },
                new SourceConfig { SourceType = SourceType.HackerNews, Label = "HN: Neon Postgres Switching", Target = "Neon Supabase postgres", Enabled = true },
                new SourceConfig { SourceType = SourceType.GitHub, Label = "GitHub: Supabase Discussions", Target = "supabase migration firebase is:issue", Enabled = true }
            ],
            Insights =
            [
                new DecisionInsight
                {
                    SourceType = SourceType.HackerNews,
                    SourceTitle = "Ask HN: Migrating off Firebase to Postgres in 2026",
                    SourceUrl = "https://news.ycombinator.com",
                    ObservedAt = now.AddDays(-2),
                    PrimaryEntity = "Supabase",
                    CompetitorsComparedCsv = "Firebase",
                    Outcome = DecisionOutcome.ChosePrimary,
                    Summary = "Team migrated from Firebase to Supabase to eliminate NoSQL vendor lock-in and gain standard SQL relational queries + self-hosting.",
                    UnmetRequirement = "First-class relational joins and predictable self-hosted escape hatch.",
                    VerbatimEvidence = "We switched from Firebase to Supabase mainly because Firestore vendor lock-in made reporting painful and we needed real Postgres SQL plus the option to self-host.",
                    GroundingVerified = true,
                    Confidence = 0.94,
                    ExtractorModel = "seeded-verified",
                    Drivers =
                    [
                        new FrictionDriver { TargetEntity = "Firebase", Category = DriverCategory.EcosystemLockin, CanonicalTheme = "vendor_lock_in", DisplayLabel = "Vendor Lock-In & Portability", RawPhrase = "Firestore vendor lock-in", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Drawback },
                        new FrictionDriver { TargetEntity = "Supabase", Category = DriverCategory.FeatureGap, CanonicalTheme = "sql_and_branching", DisplayLabel = "Native SQL & Database Branching", RawPhrase = "real Postgres SQL", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Advantage },
                        new FrictionDriver { TargetEntity = "Supabase", Category = DriverCategory.DeveloperExperience, CanonicalTheme = "self_hosting_and_local_dev", DisplayLabel = "Self-Hosting & Local Dev Experience", RawPhrase = "option to self-host", Severity = DriverSeverity.MajorFriction, Polarity = DriverPolarity.Advantage }
                    ]
                },
                new DecisionInsight
                {
                    SourceType = SourceType.HackerNews,
                    SourceTitle = "Serverless Postgres cold starts vs branching DX",
                    SourceUrl = "https://news.ycombinator.com",
                    ObservedAt = now.AddDays(-4),
                    PrimaryEntity = "Neon",
                    CompetitorsComparedCsv = "Supabase",
                    Outcome = DecisionOutcome.ChosePrimary,
                    Summary = "Developer chose Neon over Supabase for instant ephemeral database branching in CI previews, despite citing occasional cold-start latency.",
                    UnmetRequirement = "Zero-latency database branching for pull request preview environments.",
                    VerbatimEvidence = "Ended up choosing Neon over Supabase for our CI pipeline because instant database branching per PR is unbeatable, even if cold start latency occasionally bites on idle branches.",
                    GroundingVerified = true,
                    Confidence = 0.91,
                    ExtractorModel = "seeded-verified",
                    Drivers =
                    [
                        new FrictionDriver { TargetEntity = "Neon", Category = DriverCategory.FeatureGap, CanonicalTheme = "sql_and_branching", DisplayLabel = "Native SQL & Database Branching", RawPhrase = "instant database branching per PR", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Advantage },
                        new FrictionDriver { TargetEntity = "Neon", Category = DriverCategory.ReliabilityQuality, CanonicalTheme = "reliability_and_latency", DisplayLabel = "Reliability & Cold-Start Latency", RawPhrase = "cold start latency occasionally bites", Severity = DriverSeverity.MajorFriction, Polarity = DriverPolarity.Drawback }
                    ]
                },
                new DecisionInsight
                {
                    SourceType = SourceType.GitHub,
                    SourceTitle = "Pricing cliff when scaling auth MAUs and compute",
                    SourceUrl = "https://github.com",
                    ObservedAt = now.AddDays(-1),
                    PrimaryEntity = "Firebase",
                    CompetitorsComparedCsv = "Supabase",
                    Outcome = DecisionOutcome.RejectedPrimary,
                    Summary = "Startup experienced surprise billing spikes on Firebase Firestore reads during a traffic surge and began evaluating Postgres alternatives.",
                    UnmetRequirement = "Hard spending caps and predictable per-compute pricing instead of per-read billing.",
                    VerbatimEvidence = "Our biggest dealbreaker with Firebase was an unexpected $1,400 weekend bill from an unoptimized Firestore read loop with no hard spending cap.",
                    GroundingVerified = true,
                    Confidence = 0.95,
                    ExtractorModel = "seeded-verified",
                    Drivers =
                    [
                        new FrictionDriver { TargetEntity = "Firebase", Category = DriverCategory.PricingTco, CanonicalTheme = "pricing_predictability", DisplayLabel = "Pricing & TCO Predictability", RawPhrase = "unexpected $1,400 weekend bill", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Drawback }
                    ]
                }
            ]
        };

        // Starter Workspace 2: High-Consideration Hardware (Proves domain generalizability with the user's exact MED-EL vs AB example!)
        var hardware = new Workspace
        {
            Name = "High-Consideration Devices — Cochlear Systems",
            Slug = "cochlear-devices-sample",
            Description = "Demonstrates domain generalizability using first-party / manually imported customer comparison interviews (no medical forum scraping).",
            DomainCategory = "High-Consideration Hardware & Assistive Devices",
            Entities =
            [
                new TrackedEntity { Name = "Advanced Bionics", AliasesCsv = "AB,Advanced Bionics", IsPrimaryBrand = false },
                new TrackedEntity { Name = "MED-EL", AliasesCsv = "Med-el,MEDEL,Medel", IsPrimaryBrand = true },
                new TrackedEntity { Name = "Cochlear", AliasesCsv = "Nucleus,Cochlear Ltd", IsPrimaryBrand = false }
            ],
            Sources =
            [
                new SourceConfig { SourceType = SourceType.ManualPaste, Label = "First-Party Win/Loss Interviews (Manual/CSV)", Target = "Manual / First-Party Import", Enabled = true }
            ],
            Insights =
            [
                new DecisionInsight
                {
                    SourceType = SourceType.ManualPaste,
                    SourceTitle = "Patient Evaluation: MED-EL vs Advanced Bionics",
                    SourceUrl = "",
                    ObservedAt = now.AddDays(-1),
                    PrimaryEntity = "Advanced Bionics",
                    CompetitorsComparedCsv = "MED-EL",
                    Outcome = DecisionOutcome.ChosePrimary,
                    Summary = "Buyer selected Advanced Bionics over MED-EL due to native Bluetooth streaming, remote programming support, and avoiding a 7-hour one-way clinic drive.",
                    UnmetRequirement = "Remote audiology programming and native direct Bluetooth streaming to eliminate clinic travel friction.",
                    VerbatimEvidence = "Had the choice between MED-EL and AB. AB had the bluetooth... AB offered remote programming. If I went with Med-el it would be a 7h drive one way.",
                    GroundingVerified = true,
                    Confidence = 0.97,
                    ExtractorModel = "seeded-verified",
                    Drivers =
                    [
                        new FrictionDriver { TargetEntity = "Advanced Bionics", Category = DriverCategory.FeatureGap, CanonicalTheme = "bluetooth_connectivity", DisplayLabel = "Native Bluetooth Connectivity", RawPhrase = "AB had the bluetooth", Severity = DriverSeverity.MajorFriction, Polarity = DriverPolarity.Advantage },
                        new FrictionDriver { TargetEntity = "Advanced Bionics", Category = DriverCategory.ServiceSupport, CanonicalTheme = "remote_programming", DisplayLabel = "Remote Programming Support", RawPhrase = "AB offered remote programming", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Advantage },
                        new FrictionDriver { TargetEntity = "MED-EL", Category = DriverCategory.LogisticsAccess, CanonicalTheme = "clinic_travel_distance", DisplayLabel = "Clinic Travel Distance", RawPhrase = "7h drive one way", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Drawback }
                    ]
                },
                new DecisionInsight
                {
                    SourceType = SourceType.ManualPaste,
                    SourceTitle = "Reliability & Replacement Turnaround Comparison",
                    SourceUrl = "",
                    ObservedAt = now.AddDays(-5),
                    PrimaryEntity = "Cochlear",
                    CompetitorsComparedCsv = "Advanced Bionics",
                    Outcome = DecisionOutcome.ChosePrimary,
                    Summary = "User favored Cochlear over Advanced Bionics citing faster overnight warranty replacement and wider local clinic network coverage.",
                    UnmetRequirement = "Same-day or next-day hardware replacement logistics across regional clinics.",
                    VerbatimEvidence = "We went with Cochlear over AB primarily because our local audiologist stocked spare parts and warranty replacement took 24 hours instead of a week.",
                    GroundingVerified = true,
                    Confidence = 0.92,
                    ExtractorModel = "seeded-verified",
                    Drivers =
                    [
                        new FrictionDriver { TargetEntity = "Cochlear", Category = DriverCategory.ServiceSupport, CanonicalTheme = "customer_support_speed", DisplayLabel = "Customer Support & Warranty", RawPhrase = "warranty replacement took 24 hours", Severity = DriverSeverity.DealBreaker, Polarity = DriverPolarity.Advantage },
                        new FrictionDriver { TargetEntity = "Advanced Bionics", Category = DriverCategory.LogisticsAccess, CanonicalTheme = "clinic_travel_distance", DisplayLabel = "Clinic Travel Distance", RawPhrase = "local audiologist stocked spare parts", Severity = DriverSeverity.MajorFriction, Polarity = DriverPolarity.Drawback }
                    ]
                }
            ]
        };

        db.Workspaces.AddRange(devTools, hardware);
        await db.SaveChangesAsync();
    }
}
