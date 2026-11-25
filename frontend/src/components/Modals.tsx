import { useState } from 'react';
import { Copy, Check, X, FileSpreadsheet } from 'lucide-react';
import type { CreateWorkspacePayload, SourceType, WeeklyBriefDto } from '../types/api';

interface IngestModalProps {
  workspaceName: string;
  onClose: () => void;
  onSubmitManual: (title: string, body: string, sourceUrl: string) => Promise<void>;
  onSubmitCsv: (csvContent: string, ownershipConfirmed: boolean, format: 'csv' | 'jsonl') => Promise<void>;
}

export function IngestModal({ workspaceName, onClose, onSubmitManual, onSubmitCsv }: IngestModalProps) {
  const [mode, setMode] = useState<'paste' | 'bulk'>('paste');
  const [title, setTitle] = useState('');
  const [body, setBody] = useState('');
  const [sourceUrl, setSourceUrl] = useState('');

  const [bulkFormat, setBulkFormat] = useState<'csv' | 'jsonl'>('csv');
  const [bulkContent, setBulkContent] = useState('');
  const [ownershipConfirmed, setOwnershipConfirmed] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleFileSelect(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0];
    if (!file) return;
    if (file.name.endsWith('.jsonl') || file.name.endsWith('.ndjson')) {
      setBulkFormat('jsonl');
    } else {
      setBulkFormat('csv');
    }
    const text = await file.text();
    setBulkContent(text);
  }

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    setError(null);
    setBusy(true);
    try {
      if (mode === 'paste') {
        if (!body.trim()) return;
        await onSubmitManual(title, body, sourceUrl);
      } else {
        if (!bulkContent.trim() || !ownershipConfirmed) return;
        await onSubmitCsv(bulkContent, ownershipConfirmed, bulkFormat);
      }
      onClose();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Import failed');
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="ingest-title">
      <div className="modal-card">
        <div className="modal-header">
          <h2 id="ingest-title">Import into {workspaceName}</h2>
          <button type="button" className="tiny-btn" onClick={onClose} aria-label="Close">
            <X size={16} />
          </button>
        </div>

        <div className="tab-pills" style={{ marginBottom: 16 }}>
          <button
            type="button"
            className={`tab-pill ${mode === 'paste' ? 'active' : ''}`}
            onClick={() => setMode('paste')}
          >
            Single Thread Paste
          </button>
          <button
            type="button"
            className={`tab-pill ${mode === 'bulk' ? 'active' : ''}`}
            onClick={() => setMode('bulk')}
          >
            <FileSpreadsheet size={12} style={{ marginRight: 4, verticalAlign: 'middle' }} />
            Bulk CSV / JSONL
          </button>
        </div>

        {error && (
          <div
            style={{
              marginBottom: 12,
              padding: '8px 12px',
              borderRadius: 6,
              background: 'var(--accent-crimson-soft)',
              color: 'var(--accent-crimson)',
              fontSize: 12.5,
            }}
          >
            {error}
          </div>
        )}

        <form onSubmit={handleSubmit}>
          {mode === 'paste' ? (
            <>
              <div className="form-group">
                <label htmlFor="ingest-doc-title">Thread Title (optional)</label>
                <input
                  id="ingest-doc-title"
                  className="text-input"
                  placeholder="e.g., Switching from Firebase to Supabase after billing spike"
                  value={title}
                  onChange={(e) => setTitle(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="ingest-doc-body">Raw Discussion / Interview Quote / Support Ticket</label>
                <textarea
                  id="ingest-doc-body"
                  className="text-area"
                  required
                  placeholder='Paste any real user discussion (PII such as emails, phones, and @handles are automatically scrubbed before storage)...'
                  value={body}
                  onChange={(e) => setBody(e.target.value)}
                />
              </div>
              <div className="form-group">
                <label htmlFor="ingest-doc-url">Source URL (optional)</label>
                <input
                  id="ingest-doc-url"
                  className="text-input"
                  placeholder="https://..."
                  value={sourceUrl}
                  onChange={(e) => setSourceUrl(e.target.value)}
                />
              </div>
            </>
          ) : (
            <>
              <div className="form-group">
                <label htmlFor="bulk-file">Upload .csv or .jsonl file (up to 500 rows)</label>
                <input
                  id="bulk-file"
                  type="file"
                  accept=".csv,.jsonl,.ndjson,.txt"
                  onChange={(e) => void handleFileSelect(e)}
                  style={{ fontSize: 12.5 }}
                />
              </div>
              <div className="form-group">
                <label htmlFor="bulk-format">Format</label>
                <select
                  id="bulk-format"
                  className="select-input"
                  value={bulkFormat}
                  onChange={(e) => setBulkFormat(e.target.value as 'csv' | 'jsonl')}
                >
                  <option value="csv">CSV (columns: title, body/text/review, url, date)</option>
                  <option value="jsonl">JSONL (one JSON object per line)</option>
                </select>
              </div>
              <div className="form-group">
                <label htmlFor="bulk-content">Or paste CSV / JSONL content directly</label>
                <textarea
                  id="bulk-content"
                  className="text-area"
                  required
                  placeholder={'title,body,url\n"Switch reason","We migrated from Firebase to Supabase due to vendor lock-in.",https://example.com/1'}
                  value={bulkContent}
                  onChange={(e) => setBulkContent(e.target.value)}
                />
              </div>
              <div className="form-group" style={{ display: 'flex', alignItems: 'flex-start', gap: 8 }}>
                <input
                  id="attestation-check"
                  type="checkbox"
                  checked={ownershipConfirmed}
                  onChange={(e) => setOwnershipConfirmed(e.target.checked)}
                  style={{ marginTop: 3 }}
                />
                <label htmlFor="attestation-check" style={{ margin: 0, fontWeight: 500, fontSize: 12 }}>
                  I attest that this dataset is legally authorized / first-party data. Automatic PII scrubbing
                  (emails, phones, handles) will be applied before storage.
                </label>
              </div>
            </>
          )}

          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8 }}>
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            <button
              type="submit"
              className="btn btn-emerald"
              disabled={
                busy ||
                (mode === 'paste' ? !body.trim() : !bulkContent.trim() || !ownershipConfirmed)
              }
            >
              {busy ? 'Running 4-Stage Pipeline...' : 'Run Extraction Pipeline'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}

interface WeeklyBriefModalProps {
  brief: WeeklyBriefDto;
  onClose: () => void;
}

export function WeeklyBriefModal({ brief, onClose }: WeeklyBriefModalProps) {
  const [copied, setCopied] = useState(false);

  async function copyMarkdown() {
    await navigator.clipboard.writeText(brief.markdownContent);
    setCopied(true);
    setTimeout(() => setCopied(false), 1800);
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="brief-title">
      <div className="modal-card">
        <div className="modal-header">
          <h2 id="brief-title">Executive Weekly Brief (Slack / Notion Ready)</h2>
          <button type="button" className="tiny-btn" onClick={onClose} aria-label="Close">
            <X size={16} />
          </button>
        </div>
        <pre className="brief-pre">{brief.markdownContent}</pre>
        <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 14 }}>
          <button type="button" className="btn" onClick={onClose}>
            Close
          </button>
          <button type="button" className="btn btn-primary" onClick={copyMarkdown}>
            {copied ? <Check size={14} /> : <Copy size={14} />}
            {copied ? 'Copied Markdown' : 'Copy Markdown'}
          </button>
        </div>
      </div>
    </div>
  );
}

interface NewWorkspaceModalProps {
  onClose: () => void;
  onCreate: (payload: CreateWorkspacePayload) => Promise<void>;
}

export function NewWorkspaceModal({ onClose, onCreate }: NewWorkspaceModalProps) {
  const [name, setName] = useState('');
  const [domainPackId, setDomainPackId] = useState('devtools-cloud');
  const [domainCategory, setDomainCategory] = useState('SaaS & Developer Tools');
  const [description, setDescription] = useState('');
  const [entitiesText, setEntitiesText] = useState('ProductA, CompetitorB, CompetitorC');
  const [sourceType, setSourceType] = useState<SourceType>('HackerNews');
  const [sourceTarget, setSourceTarget] = useState('');
  const [busy, setBusy] = useState(false);

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault();
    const names = entitiesText
      .split(',')
      .map((s) => s.trim())
      .filter(Boolean);
    if (!name.trim() || names.length === 0) return;

    setBusy(true);
    try {
      await onCreate({
        name: name.trim(),
        description: description.trim(),
        domainCategory: domainCategory.trim(),
        domainPackId,
        entities: names.map((n, idx) => ({
          name: n,
          aliasesCsv: n.toLowerCase(),
          isPrimaryBrand: idx === 0,
        })),
        sources: sourceTarget.trim()
          ? [{ sourceType, label: `${sourceType}: ${sourceTarget.trim()}`, target: sourceTarget.trim() }]
          : [],
      });
      onClose();
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-backdrop" role="dialog" aria-modal="true" aria-labelledby="new-ws-title">
      <div className="modal-card">
        <div className="modal-header">
          <h2 id="new-ws-title">Create Generalizable Intelligence Workspace</h2>
          <button type="button" className="tiny-btn" onClick={onClose} aria-label="Close">
            <X size={16} />
          </button>
        </div>
        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label htmlFor="ws-name">Workspace Name</label>
            <input
              id="ws-name"
              className="text-input"
              required
              placeholder="e.g., Observability Platforms or E-Bike Drive Systems"
              value={name}
              onChange={(e) => setName(e.target.value)}
            />
          </div>
          <div className="form-group">
            <label htmlFor="ws-pack">Starter Domain Pack (seeds canonical taxonomy &amp; synonyms)</label>
            <select
              id="ws-pack"
              className="select-input"
              style={{ width: '100%', height: 38 }}
              value={domainPackId}
              onChange={(e) => setDomainPackId(e.target.value)}
            >
              <option value="devtools-cloud">DevTools &amp; Cloud Infrastructure</option>
              <option value="prosumer-hardware">Prosumer &amp; High-Consideration Hardware</option>
              <option value="b2b-saas">B2B SaaS &amp; Productivity</option>
              <option value="custom-blank">Custom / Any Vertical (Blank)</option>
            </select>
          </div>
          <div className="form-group">
            <label htmlFor="ws-domain">Domain / Industry Category</label>
            <input
              id="ws-domain"
              className="text-input"
              value={domainCategory}
              onChange={(e) => setDomainCategory(e.target.value)}
            />
          </div>
          <div className="form-group">
            <label htmlFor="ws-desc">Goal / Focus</label>
            <input
              id="ws-desc"
              className="text-input"
              placeholder="What switching decisions or drawbacks are you tracking?"
              value={description}
              onChange={(e) => setDescription(e.target.value)}
            />
          </div>
          <div className="form-group">
            <label htmlFor="ws-entities">Tracked Brands / Competitors (comma-separated, first = primary)</label>
            <input
              id="ws-entities"
              className="text-input"
              required
              value={entitiesText}
              onChange={(e) => setEntitiesText(e.target.value)}
            />
          </div>
          <div style={{ display: 'grid', gridTemplateColumns: '160px 1fr', gap: 10 }}>
            <div className="form-group">
              <label htmlFor="ws-source-type">Starter Connector</label>
              <select
                id="ws-source-type"
                className="select-input"
                style={{ width: '100%', height: 38 }}
                value={sourceType}
                onChange={(e) => setSourceType(e.target.value as SourceType)}
              >
                <option value="HackerNews">Hacker News</option>
                <option value="GitHub">GitHub Issues</option>
                <option value="Discourse">Discourse Forum</option>
                <option value="YouTube">YouTube API v3</option>
              </select>
            </div>
            <div className="form-group">
              <label htmlFor="ws-source-target">Query or Discourse URL (optional)</label>
              <input
                id="ws-source-target"
                className="text-input"
                placeholder={
                  sourceType === 'Discourse'
                    ? 'https://forum.example.com|search terms'
                    : sourceType === 'YouTube'
                      ? 'video:VIDEO_ID or search:query'
                      : 'e.g., Datadog Grafana pricing'
                }
                value={sourceTarget}
                onChange={(e) => setSourceTarget(e.target.value)}
              />
            </div>
          </div>
          <div style={{ display: 'flex', justifyContent: 'flex-end', gap: 8, marginTop: 8 }}>
            <button type="button" className="btn" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn btn-primary" disabled={busy}>
              {busy ? 'Creating...' : 'Create Workspace'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}
