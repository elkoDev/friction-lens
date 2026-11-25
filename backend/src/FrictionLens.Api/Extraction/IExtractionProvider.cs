using FrictionLens.Api.Domain;

namespace FrictionLens.Api.Extraction;

public sealed record ExtractedDriver(
    string TargetEntity,
    DriverCategory Category,
    string CanonicalTheme,
    string DisplayLabel,
    string RawPhrase,
    DriverSeverity Severity,
    DriverPolarity Polarity);

public sealed record ExtractionResult(
    bool IsActionable,
    string PrimaryEntity,
    IReadOnlyList<string> CompetitorsCompared,
    DecisionOutcome Outcome,
    string Summary,
    string UnmetRequirement,
    string VerbatimEvidence,
    double Confidence,
    string ModelUsed,
    IReadOnlyList<ExtractedDriver> Drivers,
    int PromptTokens = 0,
    int CompletionTokens = 0);

public sealed record ExtractionContext(
    string WorkspaceDomain,
    IReadOnlyList<TrackedEntity> Entities,
    IReadOnlyList<string> ExistingCanonicalThemes,
    string Title,
    string Body,
    string? RepairInstruction = null);

public interface IExtractionProvider
{
    string ProviderName { get; }
    string ModelName { get; }
    Task<ExtractionResult?> ExtractAsync(ExtractionContext context, CancellationToken ct);
}
