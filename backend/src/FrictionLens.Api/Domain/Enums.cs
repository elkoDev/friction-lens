using System.Text.Json.Serialization;

namespace FrictionLens.Api.Domain;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceType
{
    HackerNews,
    GitHub,
    Discourse,
    YouTube,
    ManualPaste,
    CsvImport,
    JsonlImport
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DecisionOutcome
{
    ChosePrimary,
    RejectedPrimary,
    SwitchedAway,
    Evaluating,
    Workaround
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverCategory
{
    FeatureGap,
    ServiceSupport,
    LogisticsAccess,
    PricingTco,
    ReliabilityQuality,
    EcosystemLockin,
    DeveloperExperience,
    Other
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverSeverity
{
    DealBreaker,
    MajorFriction,
    MinorAnnoyance
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DriverPolarity
{
    Advantage,
    Drawback
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TriageClass
{
    ActionableDecision,
    ProductFriction,
    NeutralQuestionOrNews,
    NoiseOrPromo
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CompliancePolicy
{
    StandardPublic,
    FirstPartyConsented,
    StrictZeroPii
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TakedownReason
{
    GdprArt17,
    AuthorRequest,
    Dmca,
    PolicyBlock
}
