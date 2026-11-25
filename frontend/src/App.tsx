import { useCallback, useEffect, useRef, useState } from 'react';
import {
  Database,
  Download,
  FileText,
  LayoutDashboard,
  Plus,
  RefreshCw,
  Search,
  Upload,
} from 'lucide-react';
import { api } from './api/client';
import { EvidenceStream } from './components/EvidenceStream';
import { FrictionRanking } from './components/FrictionRanking';
import { IngestModal, NewWorkspaceModal, WeeklyBriefModal } from './components/Modals';
import { SourcesPanel } from './components/SourcesPanel';
import { WinLossMatrix } from './components/WinLossMatrix';
import type {
  CreateWorkspacePayload,
  DeadLetterDto,
  DriverPolarity,
  DriverSeverity,
  FrictionRankingItemDto,
  IngestionJobDto,
  InsightDetailDto,
  JobProgressEvent,
  SourceType,
  SystemStatusDto,
  WeeklyBriefDto,
  WinLossMatrixDto,
  WorkspaceSummaryDto,
} from './types/api';

type ActiveView = 'overview' | 'evidence' | 'sources';

export default function App() {
  const [status, setStatus] = useState<SystemStatusDto | null>(null);
  const [workspaces, setWorkspaces] = useState<WorkspaceSummaryDto[]>([]);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [activeView, setActiveView] = useState<ActiveView>('overview');

  const [matrix, setMatrix] = useState<WinLossMatrixDto | null>(null);
  const [friction, setFriction] = useState<FrictionRankingItemDto[]>([]);
  const [insights, setInsights] = useState<InsightDetailDto[]>([]);
  const [jobs, setJobs] = useState<IngestionJobDto[]>([]);
  const [deadLetters, setDeadLetters] = useState<DeadLetterDto[]>([]);
  const [liveLogs, setLiveLogs] = useState<JobProgressEvent[]>([]);

  const [polarityFilter, setPolarityFilter] = useState<DriverPolarity | 'All'>('All');
  const [entityFilter, setEntityFilter] = useState('');
  const [severityFilter, setSeverityFilter] = useState<DriverSeverity | ''>('');
  const [searchQuery, setSearchQuery] = useState('');

  const [showIngest, setShowIngest] = useState(false);
  const [showNewWorkspace, setShowNewWorkspace] = useState(false);
  const [brief, setBrief] = useState<WeeklyBriefDto | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [toast, setToast] = useState<string | null>(null);
  const unsubSseRef = useRef<(() => void) | null>(null);

  const activeWorkspace = workspaces.find((w) => w.id === selectedId) ?? null;

  const loadWorkspaces = useCallback(async (preferredId?: string) => {
    const [st, list] = await Promise.all([api.getStatus(), api.getWorkspaces()]);
    setStatus(st);
    setWorkspaces(list);
    setSelectedId((prev) => preferredId ?? prev ?? list[0]?.id ?? null);
  }, []);

  const loadWorkspaceData = useCallback(async () => {
    if (!selectedId) return;
    const [m, f, ins, j, dl] = await Promise.all([
      api.getMatrix(selectedId),
      api.getFriction(selectedId, polarityFilter === 'All' ? undefined : polarityFilter),
      api.getInsights(selectedId, entityFilter || undefined, severityFilter || undefined, searchQuery || undefined),
      api.getJobs(selectedId),
      api.getDeadLetters(selectedId),
    ]);
    setMatrix(m);
    setFriction(f);
    setInsights(ins);
    setJobs(j);
    setDeadLetters(dl);
  }, [selectedId, polarityFilter, entityFilter, severityFilter, searchQuery]);

  useEffect(() => {
    void loadWorkspaces();
    return () => {
      unsubSseRef.current?.();
    };
  }, [loadWorkspaces]);

  useEffect(() => {
    void loadWorkspaceData();
  }, [loadWorkspaceData]);

  async function handleLiveSync() {
    if (!selectedId) return;
    unsubSseRef.current?.();
    setSyncing(true);
    setLiveLogs([]);
    setActiveView('sources');
    setToast('Live connector sync started — streaming progress via SSE...');

    try {
      const job = await api.triggerLiveSync(selectedId, 15);
      unsubSseRef.current = api.subscribeJobEvents(
        selectedId,
        job.id,
        (evt) => {
          setLiveLogs((prev) => [...prev, evt]);
          if (evt.status === 'Completed' || evt.status === 'Failed') {
            setToast(evt.message);
          }
        },
        () => {
          setSyncing(false);
          void Promise.all([loadWorkspaces(selectedId), loadWorkspaceData()]);
        },
      );
    } catch (err) {
      setSyncing(false);
      setToast(`Sync error: ${err instanceof Error ? err.message : 'Request failed'}`);
    }
  }

  async function handleManualIngest(title: string, body: string, sourceUrl: string) {
    if (!selectedId) return;
    const res = await api.ingestManual(selectedId, [{ title, body, sourceUrl }]);
    setToast(
      `Processed ${res.totalReceived} item(s): ${res.insightsCreatedCount} new grounded insight(s) (${res.deduplicatedSkipped} deduped, ${res.quarantinedCount} quarantined).`,
    );
    await Promise.all([loadWorkspaces(selectedId), loadWorkspaceData()]);
  }

  async function handleCsvIngest(csvContent: string, ownershipConfirmed: boolean, format: 'csv' | 'jsonl') {
    if (!selectedId) return;
    const res = await api.ingestCsv(selectedId, csvContent, ownershipConfirmed, format);
    setToast(
      `Bulk import complete: ${res.totalReceived} rows → ${res.passedTriageCount} passed triage → ${res.insightsCreatedCount} new insight(s) (${res.tombstonedSkipped} tombstoned, ${res.blockedByCompliance} blocked).`,
    );
    await Promise.all([loadWorkspaces(selectedId), loadWorkspaceData()]);
  }

  async function handleAddSource(sourceType: SourceType, label: string, target: string) {
    if (!selectedId) return;
    await api.addSource(selectedId, sourceType, label, target);
    setToast(`Added ${sourceType} connector "${label}".`);
    await loadWorkspaces(selectedId);
  }

  async function handleToggleSource(sourceId: string, enabled: boolean) {
    if (!selectedId) return;
    await api.toggleSource(selectedId, sourceId, enabled);
    await loadWorkspaces(selectedId);
  }

  async function handleDeleteSource(sourceId: string) {
    if (!selectedId) return;
    await api.deleteSource(selectedId, sourceId);
    setToast('Removed connector.');
    await loadWorkspaces(selectedId);
  }

  async function handleOpenBrief() {
    if (!selectedId) return;
    const data = await api.getWeeklyBrief(selectedId);
    setBrief(data);
  }

  function handleExportTeardown() {
    if (!selectedId) return;
    window.open(api.getTeardownReportUrl(selectedId), '_blank', 'noopener,noreferrer');
  }

  async function handleCreateWorkspace(payload: CreateWorkspacePayload) {
    const created = await api.createWorkspace(payload);
    setEntityFilter('');
    setSeverityFilter('');
    setSearchQuery('');
    await loadWorkspaces(created.id);
    setToast(`Created workspace "${payload.name}".`);
  }

  async function handleFeedback(insightId: string, accurate: boolean) {
    await api.flagFeedback(insightId, accurate);
    setInsights((prev) =>
      prev.map((i) => (i.id === insightId ? { ...i, userVerifiedAccurate: accurate } : i)),
    );
  }

  const groundingPct =
    matrix && matrix.totalInsights > 0
      ? Math.round((matrix.groundedInsights / matrix.totalInsights) * 100)
      : 100;

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-header">
          <div className="brand-mark">FL</div>
          <div>
            <span className="brand-title">FrictionLens</span>
            <span className="brand-sub">Decision &amp; Friction Intel</span>
          </div>
        </div>

        <div className="sidebar-section-label">Workspace Views</div>
        <nav style={{ display: 'flex', flexDirection: 'column', gap: 4 }} aria-label="Primary views">
          <button
            type="button"
            className={`workspace-btn ${activeView === 'overview' ? 'active' : ''}`}
            onClick={() => setActiveView('overview')}
            style={{ display: 'flex', alignItems: 'center', gap: 8 }}
          >
            <LayoutDashboard size={14} /> <span className="workspace-btn-name" style={{ margin: 0 }}>Overview</span>
          </button>
          <button
            type="button"
            className={`workspace-btn ${activeView === 'evidence' ? 'active' : ''}`}
            onClick={() => setActiveView('evidence')}
            style={{ display: 'flex', alignItems: 'center', gap: 8 }}
          >
            <Search size={14} /> <span className="workspace-btn-name" style={{ margin: 0 }}>Evidence Room</span>
          </button>
          <button
            type="button"
            className={`workspace-btn ${activeView === 'sources' ? 'active' : ''}`}
            onClick={() => setActiveView('sources')}
            style={{ display: 'flex', alignItems: 'center', gap: 8 }}
          >
            <Database size={14} /> <span className="workspace-btn-name" style={{ margin: 0 }}>Sources &amp; Jobs</span>
          </button>
        </nav>

        <div className="sidebar-section-label">Workspaces</div>
        <div className="workspace-list">
          {workspaces.map((ws) => (
            <button
              key={ws.id}
              type="button"
              className={`workspace-btn ${ws.id === selectedId ? 'active' : ''}`}
              onClick={() => {
                setSelectedId(ws.id);
                setEntityFilter('');
                setSeverityFilter('');
                setSearchQuery('');
              }}
            >
              <span className="workspace-btn-name">{ws.name}</span>
              <span className="workspace-btn-meta">
                {ws.insightCount} insights · {ws.dealBreakerCount} deal-breakers
              </span>
            </button>
          ))}

          <button
            type="button"
            className="sidebar-new-btn"
            onClick={() => setShowNewWorkspace(true)}
          >
            <Plus size={14} /> New Workspace
          </button>
        </div>

        {status && (
          <div className="status-box" aria-label="Runtime configuration">
            <div className="status-row">
              <span>Database</span>
              <span className="status-val">{status.databaseProvider}</span>
            </div>
            <div className="status-row">
              <span>Extractor</span>
              <span className="status-val">{status.extractionProvider}</span>
            </div>
            <div className="status-row">
              <span>Version</span>
              <span className="status-val">{status.version}</span>
            </div>
          </div>
        )}
      </aside>

      <main className="main-area">
        {toast && (
          <div className="toast-banner" role="status">
            <span>{toast}</span>
            <button type="button" className="tiny-btn" onClick={() => setToast(null)}>
              Dismiss
            </button>
          </div>
        )}

        {activeWorkspace && (
          <>
            <header className="top-bar">
              <div>
                <h1>{activeWorkspace.name}</h1>
                <p className="top-bar-desc">{activeWorkspace.description}</p>
                <div className="entity-strip">
                  {activeWorkspace.entities.map((e) => (
                    <span
                      key={e.id}
                      className={`entity-chip ${e.isPrimaryBrand ? 'primary' : ''}`}
                    >
                      {e.name}
                      {e.isPrimaryBrand ? ' (Primary)' : ''}
                    </span>
                  ))}
                </div>
              </div>

              <div className="action-group">
                <button type="button" className="btn" onClick={() => setShowIngest(true)}>
                  <Upload size={14} /> Import / Paste
                </button>
                <button
                  type="button"
                  className="btn btn-emerald"
                  disabled={syncing || activeWorkspace.sources.every((s) => s.sourceType === 'ManualPaste' || !s.enabled)}
                  onClick={handleLiveSync}
                >
                  <RefreshCw size={14} />
                  {syncing ? 'Streaming SSE...' : 'Sync Live Sources'}
                </button>
                <button type="button" className="btn" onClick={handleOpenBrief}>
                  <FileText size={14} /> Weekly Brief
                </button>
                <button type="button" className="btn btn-primary" onClick={handleExportTeardown}>
                  <Download size={14} /> Teardown Report
                </button>
              </div>
            </header>

            {activeView === 'overview' && (
              <>
                <section className="metrics-grid" aria-label="Workspace metrics">
                  <div className="metric-card">
                    <div className="metric-label">Verified Decisions</div>
                    <div className="metric-value">{matrix?.groundedInsights ?? 0}</div>
                    <div className="metric-sub">100% grounded insights</div>
                  </div>
                  <div className="metric-card">
                    <div className="metric-label">Deal-Breakers</div>
                    <div className="metric-value">{activeWorkspace.dealBreakerCount}</div>
                    <div className="metric-sub">Explicit switch triggers</div>
                  </div>
                  <div className="metric-card">
                    <div className="metric-label">Quote Grounding</div>
                    <div className="metric-value">{groundingPct}%</div>
                    <div className="metric-sub">Ordered span verified</div>
                  </div>
                  <div className="metric-card">
                    <div className="metric-label">Active Connectors</div>
                    <div className="metric-value">{activeWorkspace.sources.filter((s) => s.enabled).length}</div>
                    <div className="metric-sub">
                      {activeWorkspace.sources.map((s) => s.sourceType).join(', ') || 'Manual only'}
                    </div>
                  </div>
                </section>

                <div className="intel-grid">
                  <WinLossMatrix matrix={matrix} />
                  <FrictionRanking
                    items={friction}
                    polarityFilter={polarityFilter}
                    onFilterChange={setPolarityFilter}
                  />
                </div>

                <EvidenceStream
                  insights={insights}
                  entities={activeWorkspace.entities.map((e) => e.name)}
                  entityFilter={entityFilter}
                  severityFilter={severityFilter}
                  onEntityFilterChange={setEntityFilter}
                  onSeverityFilterChange={setSeverityFilter}
                  onFeedback={handleFeedback}
                />
              </>
            )}

            {activeView === 'evidence' && (
              <div>
                <div style={{ marginBottom: 14, display: 'flex', gap: 10 }}>
                  <input
                    id="evidence-search"
                    className="text-input"
                    placeholder="Search verbatim quotes, unmet requirements, or canonical themes..."
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                  />
                </div>
                <EvidenceStream
                  insights={insights}
                  entities={activeWorkspace.entities.map((e) => e.name)}
                  entityFilter={entityFilter}
                  severityFilter={severityFilter}
                  onEntityFilterChange={setEntityFilter}
                  onSeverityFilterChange={setSeverityFilter}
                  onFeedback={handleFeedback}
                />
              </div>
            )}

            {activeView === 'sources' && (
              <SourcesPanel
                sources={activeWorkspace.sources}
                jobs={jobs}
                deadLetters={deadLetters}
                liveLogs={liveLogs}
                onAddSource={handleAddSource}
                onToggleSource={handleToggleSource}
                onDeleteSource={handleDeleteSource}
              />
            )}
          </>
        )}
      </main>

      {showIngest && activeWorkspace && (
        <IngestModal
          workspaceName={activeWorkspace.name}
          onClose={() => setShowIngest(false)}
          onSubmitManual={handleManualIngest}
          onSubmitCsv={handleCsvIngest}
        />
      )}

      {brief && <WeeklyBriefModal brief={brief} onClose={() => setBrief(null)} />}

      {showNewWorkspace && (
        <NewWorkspaceModal
          onClose={() => setShowNewWorkspace(false)}
          onCreate={handleCreateWorkspace}
        />
      )}
    </div>
  );
}
