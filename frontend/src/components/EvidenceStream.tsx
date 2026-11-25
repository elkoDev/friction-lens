import { CheckCircle2, ExternalLink, ThumbsDown, ThumbsUp } from 'lucide-react';
import type { DriverSeverity, InsightDetailDto } from '../types/api';

interface Props {
  insights: InsightDetailDto[];
  entities: string[];
  entityFilter: string;
  severityFilter: DriverSeverity | '';
  onEntityFilterChange: (entity: string) => void;
  onSeverityFilterChange: (severity: DriverSeverity | '') => void;
  onFeedback: (insightId: string, accurate: boolean) => void;
}

export function EvidenceStream({
  insights,
  entities,
  entityFilter,
  severityFilter,
  onEntityFilterChange,
  onSeverityFilterChange,
  onFeedback,
}: Props) {
  return (
    <section className="panel" aria-labelledby="evidence-heading">
      <div className="panel-header">
        <div>
          <h2 id="evidence-heading" className="panel-title">
            Verifiable Evidence Room
          </h2>
          <span className="panel-sub">
            Every structured claim is deterministically grounded to its exact verbatim quote
          </span>
        </div>

        <div className="filter-row">
          <select
            id="entity-filter"
            className="select-input"
            value={entityFilter}
            onChange={(e) => onEntityFilterChange(e.target.value)}
            aria-label="Filter by entity"
          >
            <option value="">All Entities</option>
            {entities.map((ent) => (
              <option key={ent} value={ent}>
                {ent}
              </option>
            ))}
          </select>

          <select
            id="severity-filter"
            className="select-input"
            value={severityFilter}
            onChange={(e) => onSeverityFilterChange(e.target.value as DriverSeverity | '')}
            aria-label="Filter by severity"
          >
            <option value="">All Severities</option>
            <option value="DealBreaker">Deal-Breakers Only</option>
            <option value="MajorFriction">Major Friction</option>
            <option value="MinorAnnoyance">Minor Annoyance</option>
          </select>
        </div>
      </div>

      {insights.length === 0 ? (
        <div className="empty-state">No evidence records match the current filters.</div>
      ) : (
        <div className="evidence-list">
          {insights.map((ins) => (
            <article key={ins.id} className="evidence-card">
              <div className="evidence-meta">
                <div style={{ display: 'flex', alignItems: 'center', gap: 8, flexWrap: 'wrap' }}>
                  <span className="badge badge-neutral">{ins.sourceType}</span>
                  <strong style={{ color: 'var(--ink-primary)' }}>{ins.primaryEntity}</strong>
                  {ins.competitorsCompared.length > 0 && (
                    <span>vs. {ins.competitorsCompared.join(', ')}</span>
                  )}
                  <span className="badge badge-advantage">{ins.outcome}</span>
                </div>
                <div style={{ display: 'flex', alignItems: 'center', gap: 10 }}>
                  {ins.groundingVerified && (
                    <span
                      className="badge badge-advantage"
                      title="Verified substring match against raw source document"
                    >
                      <CheckCircle2 size={12} /> Grounded
                    </span>
                  )}
                  <span style={{ fontFamily: 'var(--font-mono)' }}>
                    {Math.round(ins.confidence * 100)}% conf
                  </span>
                </div>
              </div>

              <blockquote className="evidence-quote">“{ins.verbatimEvidence}”</blockquote>

              <p className="evidence-summary">{ins.summary}</p>

              {ins.unmetRequirement && (
                <div className="unmet-box">
                  <strong>Unmet Requirement:</strong> {ins.unmetRequirement}
                </div>
              )}

              <div className="driver-tags">
                {ins.drivers.map((d) => (
                  <span
                    key={d.id}
                    className={`badge ${
                      d.severity === 'DealBreaker'
                        ? 'badge-dealbreaker'
                        : d.polarity === 'Advantage'
                          ? 'badge-advantage'
                          : 'badge-major'
                    }`}
                  >
                    {d.targetEntity}: {d.displayLabel} ({d.severity})
                  </span>
                ))}
              </div>

              <div className="evidence-footer">
                <div style={{ display: 'flex', alignItems: 'center', gap: 12 }}>
                  <span style={{ fontFamily: 'var(--font-mono)', color: 'var(--ink-muted)' }}>
                    {ins.extractorModel}
                  </span>
                  {ins.sourceUrl && (
                    <a
                      href={ins.sourceUrl}
                      target="_blank"
                      rel="noreferrer"
                      style={{
                        display: 'inline-flex',
                        alignItems: 'center',
                        gap: 4,
                        color: 'var(--accent-emerald)',
                        textDecoration: 'none',
                        fontWeight: 600,
                      }}
                    >
                      Source <ExternalLink size={12} />
                    </a>
                  )}
                </div>

                <div className="feedback-btns">
                  <button
                    type="button"
                    className={`tiny-btn ${ins.userVerifiedAccurate === true ? 'selected-good' : ''}`}
                    onClick={() => onFeedback(ins.id, true)}
                  >
                    <ThumbsUp size={12} /> Accurate
                  </button>
                  <button
                    type="button"
                    className={`tiny-btn ${ins.userVerifiedAccurate === false ? 'selected-bad' : ''}`}
                    onClick={() => onFeedback(ins.id, false)}
                  >
                    <ThumbsDown size={12} /> Flag
                  </button>
                </div>
              </div>
            </article>
          ))}
        </div>
      )}
    </section>
  );
}
