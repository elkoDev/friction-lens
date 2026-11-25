export type SourceType =
  | 'HackerNews'
  | 'GitHub'
  | 'Discourse'
  | 'YouTube'
  | 'StackExchange'
  | 'AppStoreReviews'
  | 'ManualPaste'
  | 'CsvImport'
  | 'JsonlImport';

export type DecisionOutcome =
  | 'ChosePrimary'
  | 'RejectedPrimary'
  | 'SwitchedAway'
  | 'Evaluating'
  | 'Workaround';

export type DriverCategory =
  | 'FeatureGap'
  | 'ServiceSupport'
  | 'LogisticsAccess'
  | 'PricingTco'
  | 'ReliabilityQuality'
  | 'EcosystemLockin'
  | 'DeveloperExperience'
  | 'Other';

export type DriverSeverity = 'DealBreaker' | 'MajorFriction' | 'MinorAnnoyance';
export type DriverPolarity = 'Advantage' | 'Drawback';
export type JobStatus = 'Queued' | 'Running' | 'Completed' | 'Failed';
export type CompliancePolicy = 'StandardPublic' | 'FirstPartyConsented' | 'StrictZeroPii';

export interface EntityDto {
  id: string;
  name: string;
  aliasesCsv: string;
  isPrimaryBrand: boolean;
}

export interface SourceConfigDto {
  id: string;
  sourceType: SourceType;
  label: string;
  target: string;
  enabled: boolean;
  lastSyncedAt: string | null;
  consecutiveFailures: number;
  lastErrorMessage: string | null;
}

export interface WorkspaceSummaryDto {
  id: string;
  name: string;
  slug: string;
  description: string;
  domainCategory: string;
  domainPackId: string;
  compliancePolicy: CompliancePolicy;
  createdAt: string;
  insightCount: number;
  dealBreakerCount: number;
  entities: EntityDto[];
  sources: SourceConfigDto[];
}

export interface FrictionDriverDto {
  id: string;
  targetEntity: string;
  category: DriverCategory;
  canonicalTheme: string;
  displayLabel: string;
  rawPhrase: string;
  severity: DriverSeverity;
  polarity: DriverPolarity;
}

export interface InsightDetailDto {
  id: string;
  sourceType: SourceType;
  sourceUrl: string;
  sourceTitle: string;
  observedAt: string;
  primaryEntity: string;
  competitorsCompared: string[];
  outcome: DecisionOutcome;
  summary: string;
  unmetRequirement: string;
  verbatimEvidence: string;
  groundingVerified: boolean;
  groundingScore: number;
  groundingTier: string;
  isQuarantined: boolean;
  confidence: number;
  extractorModel: string;
  userVerifiedAccurate: boolean | null;
  drivers: FrictionDriverDto[];
}

export interface HeadToHeadCellDto {
  winnerEntity: string;
  loserEntity: string;
  winCount: number;
  topWinningDrivers: string[];
  topLosingFrictions: string[];
}

export interface WinLossMatrixDto {
  workspaceId: string;
  workspaceName: string;
  totalInsights: number;
  groundedInsights: number;
  entities: string[];
  comparisons: HeadToHeadCellDto[];
}

export interface FrictionRankingItemDto {
  canonicalTheme: string;
  displayLabel: string;
  category: DriverCategory;
  targetEntity: string;
  polarity: DriverPolarity;
  mentionCount: number;
  dealBreakerCount: number;
  sampleQuotes: string[];
}

export interface WeeklyBriefDto {
  workspaceId: string;
  workspaceName: string;
  generatedAt: string;
  insightsAnalyzed: number;
  markdownContent: string;
}

export interface BattlecardDto {
  workspaceId: string;
  primaryEntity: string;
  competitorEntity: string;
  headToHeadCount: number;
  whyWeWin: string[];
  knownGapsAndObjections: string[];
  landminesToPlant: string[];
  markdownContent: string;
  generatedAt: string;
}

export interface TrendVelocityItemDto {
  canonicalTheme: string;
  displayLabel: string;
  targetEntity: string;
  currentWindowCount: number;
  previousWindowCount: number;
  velocityPct: number;
  isSpike: boolean;
  weeklySparkline: number[];
}

export interface CanonicalThemeDto {
  id: string;
  slug: string;
  displayLabel: string;
  category: DriverCategory;
  driverCount: number;
  aliases: string[];
}

export interface MergeSuggestionDto {
  themeAId: string;
  themeALabel: string;
  themeBId: string;
  themeBLabel: string;
  similarityScore: number;
}

export interface DomainPackDto {
  packId: string;
  displayName: string;
  description: string;
  defaultDomainCategory: string;
}

export interface NotificationChannelDto {
  id: string;
  channelType: string;
  label: string;
  webhookUrlMasked: string;
  enabled: boolean;
  lastDeliveredAt: string | null;
}

export interface IngestionJobDto {
  id: string;
  status: JobStatus;
  triggerSource: string;
  currentStage: string;
  progressPercent: number;
  documentsFetched: number;
  documentsPassedFilter: number;
  insightsExtracted: number;
  errorMessage: string | null;
  createdAt: string;
  completedAt: string | null;
}

export interface JobProgressEvent {
  jobId: string;
  status: JobStatus;
  stage: string;
  progressPercent: number;
  documentsFetched: number;
  documentsPassedFilter: number;
  insightsExtracted: number;
  message: string;
  timestamp: string;
}

export interface DeadLetterDto {
  id: string;
  sourceType: SourceType;
  stage: string;
  externalId: string | null;
  errorMessage: string;
  occurredAt: string;
}

export interface PipelineRunSummary {
  totalReceived: number;
  tombstonedSkipped: number;
  deduplicatedSkipped: number;
  blockedByCompliance: number;
  passedFilterCount: number;
  passedTriageCount: number;
  insightsCreatedCount: number;
  quarantinedCount: number;
}

export interface SystemStatusDto {
  databaseProvider: string;
  extractionProvider: string;
  extractionModel: string;
  embeddingProvider: string;
  ollamaConfigured: boolean;
  cloudLlmConfigured: boolean;
  youTubeConfigured: boolean;
  version: string;
}

export interface CreateWorkspacePayload {
  name: string;
  description: string;
  domainCategory: string;
  domainPackId: string;
  entities: { name: string; aliasesCsv: string; isPrimaryBrand: boolean }[];
  sources: { sourceType: SourceType; label: string; target: string }[];
}
