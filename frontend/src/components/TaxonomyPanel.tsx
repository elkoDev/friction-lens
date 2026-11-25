import { useState } from 'react';
import { GitMerge, Plus, Sparkles } from 'lucide-react';
import type { CanonicalThemeDto, MergeSuggestionDto } from '../types/api';

interface Props {
  themes: CanonicalThemeDto[];
  suggestions: MergeSuggestionDto[];
  onMerge: (sourceIds: string[], targetId: string) => Promise<void>;
  onAddAlias: (themeId: string, phrase: string) => Promise<void>;
}

export function TaxonomyPanel({ themes, suggestions, onMerge, onAddAlias }: Props) {
  const [selectedIds, setSelectedIds] = useState<string[]>([]);
  const [aliasInputs, setAliasInputs] = useState<Record<string, string>>({});
  const [busy, setBusy] = useState(false);

  function toggleSelect(id: string) {
    setSelectedIds((prev) => (prev.includes(id) ? prev.filter((x) => x !== id) : [...prev, id]));
  }

  async function handleMergeSelected() {
    if (selectedIds.length < 2) return;
    setBusy(true);
    try {
      const [targetId, ...sourceIds] = selectedIds;
      await onMerge(sourceIds, targetId);
      setSelectedIds([]);
    } finally {
      setBusy(false);
    }
  }

  async function handleAddAlias(themeId: string) {
    const phrase = (aliasInputs[themeId] ?? '').trim();
    if (!phrase) return;
    await onAddAlias(themeId, phrase);
    setAliasInputs((prev) => ({ ...prev, [themeId]: '' }));
  }

  return (
    <div style={{ display: 'flex', flexDirection: 'column', gap: 18 }}>
      {suggestions.length > 0 && (
        <section className="panel" aria-labelledby="suggestions-heading">
          <div className="panel-header">
            <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
              <Sparkles size={15} color="var(--accent-emerald)" />
              <div>
                <h2 id="suggestions-heading" className="panel-title">
                  Embedding Merge Suggestions
                </h2>
                <span className="panel-sub">
                  Cosine-similar canonical themes detected via vector embeddings
                </span>
              </div>
            </div>
          </div>

          <div className="friction-list">
            {suggestions.map((s) => (
              <div key={`${s.themeAId}-${s.themeBId}`} className="friction-item">
                <div>
                  <div className="friction-label">
                    {s.themeALabel} ↔ {s.themeBLabel}
                  </div>
                  <div className="friction-meta">
                    <span>Cosine Similarity: {Math.round(s.similarityScore * 100)}%</span>
                  </div>
                </div>
                <button
                  type="button"
                  className="btn btn-emerald"
                  style={{ padding: '6px 11px', fontSize: 12 }}
                  disabled={busy}
                  onClick={() => void onMerge([s.themeBId], s.themeAId)}
                >
                  <GitMerge size={13} /> Merge into {s.themeALabel}
                </button>
              </div>
            ))}
          </div>
        </section>
      )}

      <section className="panel" aria-labelledby="taxonomy-heading">
        <div className="panel-header">
          <div>
            <h2 id="taxonomy-heading" className="panel-title">
              Canonical Taxonomy &amp; Aliases ({themes.length})
            </h2>
            <span className="panel-sub">
              Select ≥2 themes to merge synonyms transactionally (first selected becomes the surviving canonical theme)
            </span>
          </div>

          {selectedIds.length >= 2 && (
            <button
              type="button"
              className="btn btn-primary"
              disabled={busy}
              onClick={() => void handleMergeSelected()}
            >
              <GitMerge size={14} /> Merge {selectedIds.length} Selected
            </button>
          )}
        </div>

        {themes.length === 0 ? (
          <div className="empty-state">
            No canonical themes minted yet. Create a workspace with a Domain Pack or run ingestion.
          </div>
        ) : (
          <div className="friction-list">
            {themes.map((t) => (
              <div key={t.id} className="friction-item" style={{ flexDirection: 'column', gap: 10 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', width: '100%', alignItems: 'center' }}>
                  <label style={{ display: 'flex', alignItems: 'center', gap: 10, cursor: 'pointer' }}>
                    <input
                      type="checkbox"
                      checked={selectedIds.includes(t.id)}
                      onChange={() => toggleSelect(t.id)}
                    />
                    <div>
                      <div className="friction-label">{t.displayLabel}</div>
                      <div className="friction-meta">
                        <span>{t.slug}</span>
                        <span>·</span>
                        <span>{t.category}</span>
                        <span>·</span>
                        <span>{t.driverCount} drivers</span>
                      </div>
                    </div>
                  </label>
                </div>

                <div style={{ display: 'flex', flexWrap: 'wrap', gap: 6, alignItems: 'center', paddingLeft: 26 }}>
                  {t.aliases.map((a) => (
                    <span key={a} className="badge badge-neutral">
                      {a}
                    </span>
                  ))}
                  <div style={{ display: 'inline-flex', gap: 4, alignItems: 'center' }}>
                    <input
                      className="text-input"
                      style={{ width: 170, padding: '4px 8px', fontSize: 12 }}
                      placeholder="+ add synonym..."
                      value={aliasInputs[t.id] ?? ''}
                      onChange={(e) => setAliasInputs((prev) => ({ ...prev, [t.id]: e.target.value }))}
                    />
                    <button
                      type="button"
                      className="tiny-btn"
                      onClick={() => void handleAddAlias(t.id)}
                    >
                      <Plus size={12} /> Add
                    </button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}
