namespace FrictionLens.Api.Domain;

public sealed class Workspace
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required string Slug { get; set; }
    public string Description { get; set; } = string.Empty;
    public string DomainCategory { get; set; } = "General";
    public CompliancePolicy CompliancePolicy { get; set; } = CompliancePolicy.StandardPublic;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<TrackedEntity> Entities { get; set; } = [];
    public List<SourceConfig> Sources { get; set; } = [];
    public List<RawDocument> Documents { get; set; } = [];
    public List<DecisionInsight> Insights { get; set; } = [];
    public List<IngestionJob> Jobs { get; set; } = [];
    public List<CanonicalThemeRecord> CanonicalThemes { get; set; } = [];
}

public sealed class TrackedEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public required string Name { get; set; }
    public string AliasesCsv { get; set; } = string.Empty;
    public bool IsPrimaryBrand { get; set; }

    public IReadOnlyList<string> GetAllTerms()
    {
        var terms = new List<string> { Name.Trim() };
        if (!string.IsNullOrWhiteSpace(AliasesCsv))
        {
            terms.AddRange(
                AliasesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Where(t => t.Length > 0));
        }
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

public sealed class SourceConfig
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public SourceType SourceType { get; set; }
    public required string Label { get; set; }
    public required string Target { get; set; }
    public bool Enabled { get; set; } = true;
    public DateTimeOffset? LastSyncedAt { get; set; }

    public SourceSyncState? SyncState { get; set; }
}

public sealed class SourceSyncState
{
    public Guid SourceConfigId { get; set; }
    public SourceConfig? SourceConfig { get; set; }

    public DateTimeOffset? CursorTimestamp { get; set; }
    public string? LastExternalId { get; set; }
    public string? CursorToken { get; set; }
    public string? LastEtag { get; set; }
    public int ConsecutiveFailures { get; set; }
    public DateTimeOffset? NextEligibleSyncAt { get; set; }
    public string? LastErrorMessage { get; set; }
}

public sealed class RawDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public SourceType SourceType { get; set; }
    public required string ExternalId { get; set; }
    public required string ContentHash { get; set; }
    public string NormalizedUrlHash { get; set; } = string.Empty;
    public long SimHash64 { get; set; }
    public Guid? DuplicateOfDocumentId { get; set; }

    public string Title { get; set; } = string.Empty;
    public required string Body { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public DateTimeOffset PublishedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset IngestedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? BodyPurgedAt { get; set; }

    public int PiiRedactionsCount { get; set; }
    public bool ContainsSensitiveTerms { get; set; }
    public bool PassedSignalFilter { get; set; }
    public string? FilterReason { get; set; }
    public double TriageScore { get; set; }
    public TriageClass? TriageClass { get; set; }
}

public sealed class DecisionInsight
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public Guid? RawDocumentId { get; set; }
    public RawDocument? RawDocument { get; set; }

    public SourceType SourceType { get; set; }
    public string SourceUrl { get; set; } = string.Empty;
    public string SourceTitle { get; set; } = string.Empty;
    public DateTimeOffset ObservedAt { get; set; } = DateTimeOffset.UtcNow;

    public required string PrimaryEntity { get; set; }
    public string CompetitorsComparedCsv { get; set; } = string.Empty;
    public DecisionOutcome Outcome { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string UnmetRequirement { get; set; } = string.Empty;
    public required string VerbatimEvidence { get; set; }
    public bool GroundingVerified { get; set; }
    public double GroundingScore { get; set; } = 1.0;
    public string GroundingTier { get; set; } = "Exact";
    public bool IsQuarantined { get; set; }
    public double Confidence { get; set; }
    public string ExtractorModel { get; set; } = "heuristic-v1";
    public bool? UserVerifiedAccurate { get; set; }

    public List<FrictionDriver> Drivers { get; set; } = [];
}

public sealed class FrictionDriver
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid DecisionInsightId { get; set; }
    public DecisionInsight? DecisionInsight { get; set; }

    public Guid? CanonicalThemeId { get; set; }
    public CanonicalThemeRecord? CanonicalThemeRecord { get; set; }

    public required string TargetEntity { get; set; }
    public DriverCategory Category { get; set; }
    public required string CanonicalTheme { get; set; }
    public required string DisplayLabel { get; set; }
    public string RawPhrase { get; set; } = string.Empty;
    public DriverSeverity Severity { get; set; }
    public DriverPolarity Polarity { get; set; }
}

public sealed class CanonicalThemeRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public required string Slug { get; set; }
    public required string DisplayLabel { get; set; }
    public DriverCategory Category { get; set; }
    public Guid? MergedIntoThemeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<ThemeAlias> Aliases { get; set; } = [];
}

public sealed class ThemeAlias
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid CanonicalThemeId { get; set; }
    public CanonicalThemeRecord? CanonicalTheme { get; set; }

    public required string NormalizedPhrase { get; set; }
    public string Source { get; set; } = "Seed";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class IngestionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Workspace? Workspace { get; set; }

    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string TriggerSource { get; set; } = "Manual";
    public string CurrentStage { get; set; } = "Queued";
    public int ProgressPercent { get; set; }
    public int AttemptCount { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int DocumentsFetched { get; set; }
    public int DocumentsPassedFilter { get; set; }
    public int InsightsExtracted { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class IngestionDeadLetter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? IngestionJobId { get; set; }
    public SourceType SourceType { get; set; }
    public required string Stage { get; set; }
    public string? ExternalId { get; set; }
    public string ErrorMessage { get; set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class TakedownTombstone
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? WorkspaceId { get; set; }
    public string? NormalizedUrlHash { get; set; }
    public string? ContentHash { get; set; }
    public string? ExternalId { get; set; }
    public TakedownReason Reason { get; set; } = TakedownReason.GdprArt17;
    public string RequestedBy { get; set; } = "System";
    public int PurgedDocumentsCount { get; set; }
    public int PurgedInsightsCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DataImportAttestation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public string LegalBasis { get; set; } = "InternalFeedback";
    public bool OwnershipConfirmed { get; set; }
    public bool PiiRedactionApplied { get; set; } = true;
    public int RowCount { get; set; }
    public DateTimeOffset AttestedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UsageLedgerEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid WorkspaceId { get; set; }
    public Guid? IngestionJobId { get; set; }
    public required string Provider { get; set; }
    public required string Model { get; set; }
    public string Operation { get; set; } = "Extract";
    public int PromptTokens { get; set; }
    public int CompletionTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
