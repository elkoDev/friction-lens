import { useState } from 'react';
import { Plus, Trash2, AlertTriangle, Radio } from 'lucide-react';
import type {
  DeadLetterDto,
  IngestionJobDto,
  JobProgressEvent,
  SourceConfigDto,
  SourceType,
} from '../types/api';

interface Props {
  sources: SourceConfigDto[];
  jobs: IngestionJobDto[];
  deadLetters: DeadLetterDto[];
  liveLogs: JobProgressEvent[];
  onAddSource: (sourceType: SourceType, label: string, target: string) => Promise<void>;
  onToggleSource: (sourceId: string, enabled: boolean) => Promise<void>;
  onDeleteSource: (sourceId: string) => Promise<void>;
}

export function SourcesPanel({
  sources,
  jobs,
  deadLetters,
  liveLogs,
  onAddSource,
  onToggleSource,
  onDeleteSource,
}: Props) {
  const [sourceType, setSourceType] = useState<SourceType>('HackerNews');
  const [label, setLabel] = useState('');
  const [target, setTarget] = useState('');
  const [busy, setBusy] = useState(false);

  async function handleAdd(e: React.FormEvent) {
    e.preventDefault();
    if (!label.trim() || !target.trim()) return;
    setBusy(true);
    try {
      await onAddSource(sourceType, label.trim(), target.trim());
      setLabel('');
      setTarget('');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <section className="panel" aria-labelledby="connectors-heading">
        <div className="panel-header">
          <div>
            <h2 id="connectors-heading" className="panel-title">
              Compliant Source Connectors
            </h2>
            <span className="panel-sub">
              Rate-limited, SSRF-guarded, incremental connectors (HN, GitHub, Discourse, YouTube)
            </span>
          </div>
        </div>

        <div className="friction-list" style={{ marginBottom: 18 }}>
          {sources.length === 0 ? (
            <div className="empty-state">No connectors configured yet. Add one below or import CSV/JSONL.</div>
          ) : (
            sources.map((src) => (
              <div key={src.id} className="friction-item">
                <div>
                  <div className="friction-label">
                    {src.label}{' '}
                    <span className="badge badge-neutral" style={{ marginLeft: 6 }}>
                      {src.sourceType}
                    </span>
                  </div>
                  <div className="friction-meta">
                    <span>{src.target}</span>
                    {src.lastSyncedAt && (
                      <>
                        <span>·</span>
                        <span>Synced {new Date(src.lastSyncedAt).toLocaleString()}</span>
                      </>
                    )}
                    {src.lastErrorMessage && (
                      <>
                        <span>·</span>
                        <span style={{ color: 'var(--accent-crimson)' }}>{src.lastErrorMessage}</span>
                      </>
                    )}
                  </div>
                </div>
                <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                  <button
                    type="button"
                    className={`tiny-btn ${src.enabled ? 'selected-good' : ''}`}
                    onClick={() => void onToggleSource(src.id, !src.enabled)}
                  >
                    {src.enabled ? 'Enabled' : 'Paused'}
                  </button>
                  <button
                    type="button"
                    className="tiny-btn"
                    aria-label={`Delete ${src.label}`}
                    onClick={() => void onDeleteSource(src.id)}
                  >
                    <Trash2 size={12} />
                  </button>
                </div>
              </div>
            ))
          )}
        </div>

        <form
          onSubmit={handleAdd}
          style={{
            display: 'grid',
            gridTemplateColumns: '145px 1fr 1.4fr auto',
            gap: 8,
            alignItems: 'center',
            paddingTop: 14,
            borderTop: '1px solid var(--border-hairline)',
          }}
        >
          <select
            className="select-input"
            value={sourceType}
            onChange={(e) => setSourceType(e.target.value as SourceType)}
            aria-label="Connector type"
          >
            <option value="HackerNews">Hacker News</option>
            <option value="GitHub">GitHub Issues</option>
            <option value="Discourse">Discourse Forum</option>
            <option value="YouTube">YouTube API v3</option>
            <option value="StackExchange">Stack Exchange</option>
            <option value="AppStoreReviews">App Store Reviews</option>
          </select>
          <input
            className="text-input"
            placeholder="Label (e.g., HN Switching)"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            required
          />
          <input
            className="text-input"
            placeholder={
              sourceType === 'Discourse'
                ? 'https://forum.example.com|search query'
                : sourceType === 'YouTube'
                  ? 'video:VIDEO_ID or search:query'
                  : 'Search query (e.g., Supabase Firebase)'
            }
            value={target}
            onChange={(e) => setTarget(e.target.value)}
            required
          />
          <button type="submit" className="btn btn-primary" disabled={busy}>
            <Plus size={14} /> Add
          </button>
        </form>
      </section>

      {liveLogs.length > 0 && (
        <section className="panel" aria-label="Live SSE job stream">
          <div className="panel-header">
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Radio size={15} color="var(--accent-emerald)" />
              <h2 className="panel-title">Live Sync Stream (SSE)</h2>
            </div>
            <span className="badge badge-advantage">
              {liveLogs[liveLogs.length - 1].progressPercent}% · {liveLogs[liveLogs.length - 1].stage}
            </span>
          </div>
          <div className="brief-pre" style={{ maxHeight: 200 }}>
            {liveLogs.map((evt, idx) => (
              <div key={idx}>
                [{new Date(evt.timestamp).toLocaleTimeString()}] [{evt.stage}] {evt.message}
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="panel" aria-labelledby="jobs-heading">
        <div className="panel-header">
          <div>
            <h2 id="jobs-heading" className="panel-title">
              Recent Ingestion Jobs
            </h2>
            <span className="panel-sub">Funnel telemetry: Fetched → Passed Filter → Grounded Insights</span>
          </div>
        </div>

        {jobs.length === 0 ? (
          <div className="empty-state">No background sync jobs run yet. Click "Sync Live Sources" above.</div>
        ) : (
          <div className="friction-list">
            {jobs.map((j) => (
              <div key={j.id} className="friction-item">
                <div>
                  <div className="friction-label">
                    {j.triggerSource} ·{' '}
                    <span
                      className={`badge ${
                        j.status === 'Completed'
                          ? 'badge-advantage'
                          : j.status === 'Failed'
                            ? 'badge-dealbreaker'
                            : 'badge-major'
                      }`}
                    >
                      {j.status}
                    </span>
                  </div>
                  <div className="friction-meta">
                    <span>Fetched: {j.documentsFetched}</span>
                    <span>·</span>
                    <span>Passed Filter: {j.documentsPassedFilter}</span>
                    <span>·</span>
                    <span>Extracted: {j.insightsExtracted}</span>
                    <span>·</span>
                    <span>{new Date(j.createdAt).toLocaleString()}</span>
                  </div>
                  {j.errorMessage && (
                    <div style={{ fontSize: 12, color: 'var(--accent-crimson)', marginTop: 4 }}>
                      {j.errorMessage}
                    </div>
                  )}
                </div>
                <span className="badge badge-neutral">{j.progressPercent}%</span>
              </div>
            ))}
          </div>
        )}
      </section>

      {deadLetters.length > 0 && (
        <section className="panel" aria-labelledby="dlq-heading">
          <div className="panel-header">
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <AlertTriangle size={15} color="var(--accent-crimson)" />
              <h2 id="dlq-heading" className="panel-title">
                Dead-Letter Errors ({deadLetters.length})
              </h2>
            </div>
          </div>
          <div className="friction-list">
            {deadLetters.map((dl) => (
              <div key={dl.id} className="friction-item">
                <div>
                  <div className="friction-label">
                    {dl.sourceType} ({dl.stage}) — {dl.externalId}
                  </div>
                  <div className="friction-meta" style={{ color: 'var(--accent-crimson)' }}>
                    {dl.errorMessage}
                  </div>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}
    </div>
  );
}
