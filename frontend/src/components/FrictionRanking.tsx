import type { DriverPolarity, FrictionRankingItemDto } from '../types/api';

interface Props {
  items: FrictionRankingItemDto[];
  polarityFilter: DriverPolarity | 'All';
  onFilterChange: (next: DriverPolarity | 'All') => void;
}

export function FrictionRanking({ items, polarityFilter, onFilterChange }: Props) {
  return (
    <section className="panel" aria-labelledby="friction-heading">
      <div className="panel-header">
        <div>
          <h2 id="friction-heading" className="panel-title">
            Canonical Drivers &amp; Friction
          </h2>
          <span className="panel-sub">Normalized themes ranked by deal-breaker impact</span>
        </div>

        <div className="tab-pills" role="tablist">
          {(['All', 'Drawback', 'Advantage'] as const).map((tab) => (
            <button
              key={tab}
              type="button"
              role="tab"
              aria-selected={polarityFilter === tab}
              className={`tab-pill ${polarityFilter === tab ? 'active' : ''}`}
              onClick={() => onFilterChange(tab)}
            >
              {tab === 'All' ? 'All' : tab === 'Drawback' ? 'Friction' : 'Strengths'}
            </button>
          ))}
        </div>
      </div>

      {items.length === 0 ? (
        <div className="empty-state">No drivers match this filter yet.</div>
      ) : (
        <div className="friction-list">
          {items.slice(0, 8).map((item) => (
            <div
              key={`${item.canonicalTheme}-${item.targetEntity}-${item.polarity}`}
              className="friction-item"
            >
              <div>
                <div className="friction-label">{item.displayLabel}</div>
                <div className="friction-meta">
                  <span>{item.targetEntity}</span>
                  <span>·</span>
                  <span>{item.category}</span>
                  <span>·</span>
                  <span>{item.canonicalTheme}</span>
                </div>
              </div>
              <div style={{ display: 'flex', gap: 6, alignItems: 'center' }}>
                {item.dealBreakerCount > 0 && (
                  <span className="badge badge-dealbreaker">
                    {item.dealBreakerCount} deal-breaker
                  </span>
                )}
                <span
                  className={`badge ${
                    item.polarity === 'Advantage' ? 'badge-advantage' : 'badge-major'
                  }`}
                >
                  {item.mentionCount}×
                </span>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
