import type {
  CreateWorkspacePayload,
  DeadLetterDto,
  DriverPolarity,
  DriverSeverity,
  FrictionRankingItemDto,
  IngestionJobDto,
  InsightDetailDto,
  JobProgressEvent,
  PipelineRunSummary,
  SourceConfigDto,
  SourceType,
  SystemStatusDto,
  UsageSummaryDto,
  WeeklyBriefDto,
  WinLossMatrixDto,
  WorkspaceSummaryDto,
} from '../types/api';

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, {
    headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) },
    ...init,
  });
  if (!res.ok) {
    const text = await res.text();
    throw new Error(text || `HTTP ${res.status}`);
  }
  if (res.status === 204) return undefined as T;
  return res.json() as Promise<T>;
}

export const api = {
  getStatus: () => request<SystemStatusDto>('/api/status'),
  getWorkspaces: () => request<WorkspaceSummaryDto[]>('/api/workspaces'),
  createWorkspace: (payload: CreateWorkspacePayload) =>
    request<{ id: string; slug: string }>('/api/workspaces', {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  addSource: (workspaceId: string, sourceType: SourceType, label: string, target: string) =>
    request<SourceConfigDto>(`/api/workspaces/${workspaceId}/sources`, {
      method: 'POST',
      body: JSON.stringify({ sourceType, label, target }),
    }),
  toggleSource: (workspaceId: string, sourceId: string, enabled: boolean) =>
    request<SourceConfigDto>(`/api/workspaces/${workspaceId}/sources/${sourceId}`, {
      method: 'PATCH',
      body: JSON.stringify({ enabled }),
    }),
  deleteSource: (workspaceId: string, sourceId: string) =>
    request<void>(`/api/workspaces/${workspaceId}/sources/${sourceId}`, {
      method: 'DELETE',
    }),
  getMatrix: (workspaceId: string) =>
    request<WinLossMatrixDto>(`/api/workspaces/${workspaceId}/matrix`),
  getFriction: (workspaceId: string, polarity?: DriverPolarity) => {
    const qs = polarity ? `?polarity=${polarity}` : '';
    return request<FrictionRankingItemDto[]>(`/api/workspaces/${workspaceId}/friction${qs}`);
  },
  getInsights: (workspaceId: string, entity?: string, severity?: DriverSeverity, q?: string) => {
    const params = new URLSearchParams();
    if (entity) params.set('entity', entity);
    if (severity) params.set('severity', severity);
    if (q) params.set('q', q);
    const qs = params.toString() ? `?${params.toString()}` : '';
    return request<InsightDetailDto[]>(`/api/workspaces/${workspaceId}/insights${qs}`);
  },
  getWeeklyBrief: (workspaceId: string) =>
    request<WeeklyBriefDto>(`/api/workspaces/${workspaceId}/brief`),
  getTeardownReportUrl: (workspaceId: string) => `/api/workspaces/${workspaceId}/report/html`,
  getJobs: (workspaceId: string) =>
    request<IngestionJobDto[]>(`/api/workspaces/${workspaceId}/jobs`),
  getDeadLetters: (workspaceId: string) =>
    request<DeadLetterDto[]>(`/api/workspaces/${workspaceId}/dead-letters`),
  getUsage: (workspaceId: string) =>
    request<UsageSummaryDto>(`/api/workspaces/${workspaceId}/usage`),
  triggerLiveSync: (workspaceId: string, maxItemsPerSource = 20) =>
    request<IngestionJobDto>(`/api/workspaces/${workspaceId}/ingest/sync`, {
      method: 'POST',
      body: JSON.stringify({ maxItemsPerSource }),
    }),
  subscribeJobEvents: (
    workspaceId: string,
    jobId: string,
    onEvent: (evt: JobProgressEvent) => void,
    onDone: () => void,
  ): (() => void) => {
    const es = new EventSource(`/api/workspaces/${workspaceId}/jobs/${jobId}/events`);
    es.addEventListener('job-progress', (ev) => {
      try {
        const parsed = JSON.parse((ev as MessageEvent<string>).data) as JobProgressEvent;
        onEvent(parsed);
        if (parsed.status === 'Completed' || parsed.status === 'Failed') {
          es.close();
          onDone();
        }
      } catch {
        // Ignore malformed SSE frame
      }
    });
    es.onerror = () => {
      es.close();
      onDone();
    };
    return () => es.close();
  },
  ingestManual: (
    workspaceId: string,
    items: { title?: string; body: string; sourceUrl?: string }[],
    sourceType: SourceType = 'ManualPaste',
  ) =>
    request<PipelineRunSummary>(`/api/workspaces/${workspaceId}/ingest/manual`, {
      method: 'POST',
      body: JSON.stringify({ items, sourceType }),
    }),
  ingestCsv: (
    workspaceId: string,
    csvContent: string,
    ownershipConfirmed: boolean,
    format: 'csv' | 'jsonl' = 'csv',
    legalBasis = 'InternalFeedback',
  ) =>
    request<PipelineRunSummary>(`/api/workspaces/${workspaceId}/ingest/csv`, {
      method: 'POST',
      body: JSON.stringify({ csvContent, ownershipConfirmed, format, legalBasis }),
    }),
  flagFeedback: (insightId: string, accurate: boolean) =>
    request<{ id: string; userVerifiedAccurate: boolean }>(`/api/insights/${insightId}/feedback`, {
      method: 'PATCH',
      body: JSON.stringify({ accurate }),
    }),
};
