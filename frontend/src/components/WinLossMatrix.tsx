import { ArrowRight } from 'lucide-react';
import type { WinLossMatrixDto } from '../types/api';

interface Props {
  matrix: WinLossMatrixDto | null;
}

export function WinLossMatrix({ matrix }: Props) {
  return (
    <section className="panel" aria-labelledby="win-loss-heading">
      <div className="panel-header">
        <div>
          <h2 id="win-loss-heading" className="panel-title">
            Head-to-Head Switching Matrix
          </h2>
          <span className="panel-sub">Revealed-preference outcomes backed by verbatim quotes</span>
        </div>
      </div>

      {!matrix || matrix.comparisons.length === 0 ? (
        <div className="empty-state">
          No head-to-head switching pairs extracted yet. Run a live sync or paste comparison threads.
        </div>
      ) : (
        <div className="h2h-list">
          {matrix.comparisons.map((cell) => (
            <div key={`${cell.winnerEntity}-${cell.loserEntity}`} className="h2h-row">
              <div className="h2h-top">
                <div className="h2h-pair">
                  <span className="h2h-winner">{cell.winnerEntity}</span>
                  <ArrowRight size={14} color="#7c828f" />
                  <span className="h2h-loser">{cell.loserEntity}</span>
                </div>
                <span className="h2h-count">
                  {cell.winCount} {cell.winCount === 1 ? 'signal' : 'signals'}
                </span>
              </div>
              <div className="h2h-drivers">
                <div>
                  <strong>Won on</strong>
                  {cell.topWinningDrivers.length > 0 ? cell.topWinningDrivers.join(' · ') : 'General preference'}
                </div>
                <div>
                  <strong>Rival Friction</strong>
                  {cell.topLosingFrictions.length > 0 ? cell.topLosingFrictions.join(' · ') : 'None cited'}
                </div>
              </div>
            </div>
          ))}
        </div>
      )}
    </section>
  );
}
