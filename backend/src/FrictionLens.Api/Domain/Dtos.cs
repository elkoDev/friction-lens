namespace FrictionLens.Api.Domain;

public sealed record EntityDto(Guid Id, string Name, string AliasesCsv, bool IsPrimaryBrand);

public sealed record SourceConfigDto(
    Guid Id,
    SourceType SourceType,
    string Label,
    string Target,
    bool Enabled,
    DateTimeOffset? LastSyncedAt,
    int ConsecutiveFailures = 0,
    string? LastErrorMessage = null);

public sealed record WorkspaceSummaryDto(
    Guid Id,
    string Name,
    string Slug,
    string Description,
    string DomainCategory,
    CompliancePolicy CompliancePolicy,
    DateTimeOffset CreatedAt,
    int InsightCount,
    int DealBreakerCount,
    IReadOnlyList<EntityDto> Entities,
    IReadOnlyList<SourceConfigDto> Sources);

public sealed record CreateEntityInput(string Name, string AliasesCsv, bool IsPrimaryBrand);
public sealed record CreateSourceInput(SourceType SourceType, string Label, string Target);

public sealed record CreateWorkspaceRequest(
    string Name,
    string Description,
    string DomainCategory,
    IReadOnlyList<CreateEntityInput> Entities,
    IReadOnlyList<CreateSourceInput>? Sources,
    CompliancePolicy CompliancePolicy = CompliancePolicy.StandardPublic);

public sealed record AddSourceRequest(SourceType SourceType, string Label, string Target);
public sealed record ToggleSourceRequest(bool Enabled);

public sealed record ManualIngestItem(
    string? Title,
    string Body,
    string? SourceUrl,
    DateTimeOffset? PublishedAt);

public sealed record ManualIngestRequest(
    IReadOnlyList<ManualIngestItem> Items,
    SourceType SourceType = SourceType.ManualPaste);

public sealed record BulkCsvIngestRequest(
    string CsvContent,
    bool OwnershipConfirmed,
    string LegalBasis = "InternalFeedback",
    string Format = "csv");

public sealed record TriggerIngestionRequest(int MaxItemsPerSource = 25);

public sealed record FrictionDriverDto(
    Guid Id,
    string TargetEntity,
    DriverCategory Category,
    string CanonicalTheme,
    string DisplayLabel,
    string RawPhrase,
    DriverSeverity Severity,
    DriverPolarity Polarity);

public sealed record InsightDetailDto(
    Guid Id,
    SourceType SourceType,
    string SourceUrl,
    string SourceTitle,
    DateTimeOffset ObservedAt,
    string PrimaryEntity,
    IReadOnlyList<string> CompetitorsCompared,
    DecisionOutcome Outcome,
    string Summary,
    string UnmetRequirement,
    string VerbatimEvidence,
    bool GroundingVerified,
    double GroundingScore,
    string GroundingTier,
    bool IsQuarantined,
    double Confidence,
    string ExtractorModel,
    bool? UserVerifiedAccurate,
    IReadOnlyList<FrictionDriverDto> Drivers);

public sealed record FeedbackFlagRequest(bool Accurate);

public sealed record HeadToHeadCellDto(
    string WinnerEntity,
    string LoserEntity,
    int WinCount,
    IReadOnlyList<string> TopWinningDrivers,
    IReadOnlyList<string> TopLosingFrictions);

public sealed record WinLossMatrixDto(
    Guid WorkspaceId,
    string WorkspaceName,
    int TotalInsights,
    int GroundedInsights,
    IReadOnlyList<string> Entities,
    IReadOnlyList<HeadToHeadCellDto> Comparisons);

public sealed record FrictionRankingItemDto(
    string CanonicalTheme,
    string DisplayLabel,
    DriverCategory Category,
    string TargetEntity,
    DriverPolarity Polarity,
    int MentionCount,
    int DealBreakerCount,
    IReadOnlyList<string> SampleQuotes);

public sealed record WeeklyBriefDto(
    Guid WorkspaceId,
    string WorkspaceName,
    DateTimeOffset GeneratedAt,
    int InsightsAnalyzed,
    string MarkdownContent);

public sealed record IngestionJobDto(
    Guid Id,
    JobStatus Status,
    string TriggerSource,
    string CurrentStage,
    int ProgressPercent,
    int DocumentsFetched,
    int DocumentsPassedFilter,
    int InsightsExtracted,
    string? ErrorMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record JobProgressEvent(
    Guid JobId,
    JobStatus Status,
    string Stage,
    int ProgressPercent,
    int DocumentsFetched,
    int DocumentsPassedFilter,
    int InsightsExtracted,
    string Message,
    DateTimeOffset Timestamp);

public sealed record DeadLetterDto(
    Guid Id,
    SourceType SourceType,
    string Stage,
    string? ExternalId,
    string ErrorMessage,
    DateTimeOffset OccurredAt);

public sealed record TakedownRequest(
    Guid? WorkspaceId,
    string? SourceUrl,
    string? ExternalId,
    Guid? RawDocumentId,
    TakedownReason Reason = TakedownReason.GdprArt17,
    string RequestedBy = "Operator");

public sealed record TakedownResponse(
    Guid TombstoneId,
    int PurgedDocumentsCount,
    int PurgedInsightsCount);

public sealed record CanonicalThemeDto(
    Guid Id,
    string Slug,
    string DisplayLabel,
    DriverCategory Category,
    IReadOnlyList<string> Aliases);

public sealed record AddAliasRequest(string Phrase);

public sealed record UsageSummaryDto(
    int TotalCalls,
    long TotalPromptTokens,
    long TotalCompletionTokens,
    decimal EstimatedTotalUsd);

public sealed record SystemStatusDto(
    string DatabaseProvider,
    string ExtractionProvider,
    string ExtractionModel,
    bool OllamaConfigured,
    bool CloudLlmConfigured,
    bool YouTubeConfigured,
    string Version);
