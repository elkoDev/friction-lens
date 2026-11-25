namespace FrictionLens.Api.Domain;

/// <summary>
/// Out-of-the-box generalizable Domain Packs that seed canonical taxonomy themes and synonyms
/// when creating a workspace — eliminating cold-start fragmentation without hardcoding verticals.
/// </summary>
public static class DomainPackRegistry
{
    public static readonly IReadOnlyList<DomainPackDto> Packs =
    [
        new DomainPackDto(
            "devtools-cloud",
            "DevTools & Cloud Infrastructure",
            "Optimized for databases, BaaS, observability, CI/CD, and developer APIs.",
            "B2B Developer Tools & Cloud Infrastructure",
            [
                new("pricing_predictability", "Pricing & Usage Cliffs", DriverCategory.PricingTco, ["bill shock", "surprise bill", "egress cost", "pricing cliff"]),
                new("vendor_lock_in", "Vendor Lock-In & Portability", DriverCategory.EcosystemLockin, ["proprietary lock-in", "hard to migrate", "vendor lock"]),
                new("reliability_and_latency", "Reliability & Cold-Start Latency", DriverCategory.ReliabilityQuality, ["cold starts", "wake from idle", "outage", "timeout"]),
                new("self_hosting_and_local_dev", "Self-Hosting & Local Dev", DriverCategory.DeveloperExperience, ["self-host", "docker local", "on-prem"]),
                new("sql_and_branching", "Native SQL & Database Branching", DriverCategory.FeatureGap, ["postgres sql", "pr branching", "relational joins"])
            ]),
        new DomainPackDto(
            "prosumer-hardware",
            "Prosumer & High-Consideration Hardware",
            "Optimized for assistive devices, consumer electronics, EVs, solar, and audio gear.",
            "High-Consideration Hardware & Devices",
            [
                new("remote_programming", "Remote Configuration & Support", DriverCategory.ServiceSupport, ["tele-audiology", "remote fitting", "online mapping", "remote tuning"]),
                new("bluetooth_connectivity", "Native Wireless / Bluetooth", DriverCategory.FeatureGap, ["direct audio", "bluetooth streaming", "wireless pairing"]),
                new("clinic_travel_distance", "Service & Logistics Access", DriverCategory.LogisticsAccess, ["long drive", "clinic commute", "local dealer distance"]),
                new("battery_life", "Battery Life & Charging", DriverCategory.ReliabilityQuality, ["battery degradation", "charging speed", "rechargeable"]),
                new("customer_support_speed", "Warranty & Replacement Speed", DriverCategory.ServiceSupport, ["overnight replacement", "warranty turnaround", "spare parts"])
            ]),
        new DomainPackDto(
            "b2b-saas",
            "B2B SaaS & Productivity",
            "Optimized for CRM, HRIS, analytics, project management, and vertical SaaS.",
            "B2B SaaS",
            [
                new("pricing_predictability", "Pricing & Enterprise SSO Tax", DriverCategory.PricingTco, ["sso tax", "seat price", "enterprise tier jump"]),
                new("integrations_and_reporting", "Integrations & Custom Reporting", DriverCategory.FeatureGap, ["salesforce sync", "custom dashboard", "api webhook"]),
                new("dx_and_documentation", "Onboarding & Admin UX", DriverCategory.DeveloperExperience, ["slow onboarding", "steep learning curve", "admin permissions"]),
                new("customer_support_speed", "Support SLA & Account Mgmt", DriverCategory.ServiceSupport, ["slow ticket response", "dedicated csm", "support quality"])
            ]),
        new DomainPackDto(
            "custom-blank",
            "Custom / Any Vertical",
            "Blank slate — auto-bootstraps canonical themes from your ingested threads and CSVs.",
            "General",
            [])
    ];

    public static DomainPackDto GetOrDefault(string? packId)
        => Packs.FirstOrDefault(p => p.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase)) ?? Packs[^1];
}
