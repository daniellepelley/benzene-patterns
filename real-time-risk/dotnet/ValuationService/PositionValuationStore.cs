using System.Collections.Concurrent;
using Benzene.Patterns.RealTimeRisk.Contracts;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// The latest valuation per symbol: what every exposed book's position was worth at the most recent
/// mark this service saw. In-memory only (resets on restart), mirroring
/// <c>RiskReadModels/BookPositionsStore.cs</c> - see real-time-risk/README.md for why a real store
/// isn't needed to prove the pattern yet. Every value here is recomputed from scratch on the next
/// <see cref="BarClosed"/>, so a restart costs at most one bar interval of staleness.
/// </summary>
public class PositionValuationStore
{
    private readonly ConcurrentDictionary<string, SymbolValuationsResponse> _bySymbol = new(StringComparer.Ordinal);

    /// <summary>
    /// Records the valuations struck against <paramref name="bar"/>.
    /// </summary>
    /// <remarks>
    /// Last-window-wins rather than last-write-wins: <c>bar-closed</c> delivery is at-least-once and
    /// the aggregator can legitimately republish a window, so a bar whose window is not newer than
    /// the one already stored is ignored. That makes reprocessing the same bar a no-op and stops an
    /// out-of-order redelivery from rewinding the mark to a stale price - the idempotency
    /// docs/patterns/choreography.md requires of every reaction, expressed in the terms this
    /// particular reaction has available.
    /// </remarks>
    public void Record(BarClosed bar, List<PositionValuation> valuations)
    {
        // MarkedAt is the stored bar's own WindowEnd, so "is this bar newer than what we have?" is a
        // single comparison.
        _bySymbol.AddOrUpdate(
            bar.Symbol,
            _ => Build(bar, valuations),
            (_, existing) => bar.WindowEnd > existing.MarkedAt ? Build(bar, valuations) : existing);
    }

    /// <summary>Reads a symbol's latest valuations. A symbol nothing has marked yet returns an empty result.</summary>
    public SymbolValuationsResponse Query(string symbol) =>
        _bySymbol.TryGetValue(symbol, out var valuations)
            ? valuations
            : new SymbolValuationsResponse { Symbol = symbol, Valuations = new List<PositionValuation>() };

    private static SymbolValuationsResponse Build(BarClosed bar, List<PositionValuation> valuations) => new()
    {
        Symbol = bar.Symbol,
        MarkPrice = bar.Close,
        MarkedAt = bar.WindowEnd,
        Valuations = valuations
    };
}
