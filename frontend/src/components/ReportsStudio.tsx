import { useState } from 'react';
import { Check, Copy, Download, Send, Swords } from 'lucide-react';
import type { BattlecardDto, NotificationChannelDto } from '../types/api';

interface Props {
  entities: string[];
  channels: NotificationChannelDto[];
  teardownUrl: string;
  onGenerateBattlecard: (primary: string, competitor: string) => Promise<BattlecardDto>;
  onAddSlackChannel: (label: string, webhookUrl: string) => Promise<void>;
  onSendSlackDigest: () => Promise<void>;
}

export function ReportsStudio({
  entities,
  channels,
  teardownUrl,
  onGenerateBattlecard,
  onAddSlackChannel,
  onSendSlackDigest,
}: Props) {
  const [primary, setPrimary] = useState(entities[0] ?? '');
  const [competitor, setCompetitor] = useState(entities[1] ?? entities[0] ?? '');
  const [battlecard, setBattlecard] = useState<BattlecardDto | null>(null);
  const [copied, setCopied] = useState(false);
  const [busy, setBusy] = useState(false);

  const [slackLabel, setSlackLabel] = useState('#product-intel');
  const [slackUrl, setSlackUrl] = useState('');

  async function handleGenerate() {
    if (!primary || !competitor) return;
    setBusy(true);
    try {
      const card = await onGenerateBattlecard(primary, competitor);
      setBattlecard(card);
    } finally {
      setBusy(false);
    }
  }

  async function handleCopy() {
    if (!battlecard) return;
    await navigator.clipboard.writeText(battlecard.markdownContent);
    setCopied(true);
    setTimeout(() => setCopied(false), 1800);
  }

  async function handleSaveSlack(e: React.FormEvent) {
    e.preventDefault();
    if (!slackLabel.trim() || !slackUrl.trim()) return;
    await onAddSlackChannel(slackLabel.trim(), slackUrl.trim());
    setSlackUrl('');
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      <section className="panel" aria-labelledby="battlecard-heading">
        <div className="panel-header">
          <div>
            <h2 id="battlecard-heading" className="panel-title">
              Sales Battlecard &amp; Trojan Horse Studio
            </h2>
            <span className="panel-sub">
              Synthesize strictly grounded head-to-head objection handling, landmines, and executive PDFs
            </span>
          </div>
          <a
            href={teardownUrl}
            target="_blank"
            rel="noreferrer"
            className="btn btn-primary"
            style={{ textDecoration: 'none' }}
          >
            <Download size={14} /> Open Print-Ready Teardown Report
          </a>
        </div>

        <div style={{ display: 'flex', gap: 10, alignItems: 'center', flexWrap: 'wrap', marginBottom: 16 }}>
          <select
            className="select-input"
            value={primary}
            onChange={(e) => setPrimary(e.target.value)}
            aria-label="Primary entity"
          >
            {entities.map((e) => (
              <option key={e} value={e}>
                Our Brand: {e}
              </option>
            ))}
          </select>
          <span style={{ fontWeight: 700, fontSize: 12, color: 'var(--ink-muted)' }}>VS</span>
          <select
            className="select-input"
            value={competitor}
            onChange={(e) => setCompetitor(e.target.value)}
            aria-label="Competitor entity"
          >
            {entities.map((e) => (
              <option key={e} value={e}>
                Competitor: {e}
              </option>
            ))}
          </select>
          <button
            type="button"
            className="btn btn-emerald"
            disabled={busy || !primary || !competitor || primary === competitor}
            onClick={() => void handleGenerate()}
          >
            <Swords size={14} /> {busy ? 'Building...' : 'Generate Battlecard'}
          </button>
          {battlecard && (
            <button type="button" className="btn" onClick={() => void handleCopy()}>
              {copied ? <Check size={14} /> : <Copy size={14} />}
              {copied ? 'Copied Markdown' : 'Copy Markdown'}
            </button>
          )}
        </div>

        {battlecard && <pre className="brief-pre">{battlecard.markdownContent}</pre>}
      </section>

      <section className="panel" aria-labelledby="slack-heading">
        <div className="panel-header">
          <div>
            <h2 id="slack-heading" className="panel-title">
              Push-First Slack Digest Webhooks
            </h2>
            <span className="panel-sub">
              Deliver Block Kit weekly briefs directly into #product-intel or #pmm (SSRF-guarded HTTPS)
            </span>
          </div>
          {channels.length > 0 && (
            <button type="button" className="btn btn-emerald" onClick={() => void onSendSlackDigest()}>
              <Send size={14} /> Push Weekly Digest Now
            </button>
          )}
        </div>

        {channels.length > 0 && (
          <div className="friction-list" style={{ marginBottom: 14 }}>
            {channels.map((ch) => (
              <div key={ch.id} className="friction-item">
                <div>
                  <div className="friction-label">{ch.label}</div>
                  <div className="friction-meta">
                    <span>{ch.webhookUrlMasked}</span>
                    {ch.lastDeliveredAt && (
                      <>
                        <span>·</span>
                        <span>Last sent {new Date(ch.lastDeliveredAt).toLocaleString()}</span>
                      </>
                    )}
                  </div>
                </div>
                <span className="badge badge-advantage">{ch.channelType}</span>
              </div>
            ))}
          </div>
        )}

        <form
          onSubmit={handleSaveSlack}
          style={{ display: 'grid', gridTemplateColumns: '180px 1fr auto', gap: 8, alignItems: 'center' }}
        >
          <input
            className="text-input"
            placeholder="#product-intel"
            value={slackLabel}
            onChange={(e) => setSlackLabel(e.target.value)}
            required
          />
          <input
            className="text-input"
            placeholder="https://hooks.slack.com/services/..."
            value={slackUrl}
            onChange={(e) => setSlackUrl(e.target.value)}
            required
          />
          <button type="submit" className="btn btn-primary">
            Save Webhook
          </button>
        </form>
      </section>
    </div>
  );
}
