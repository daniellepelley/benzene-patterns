namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>
/// The <see cref="Topics.BarClosed"/> event: one symbol's OHLC bar for a completed time window, as
/// rolled up by the Market-Data Aggregator and published for anyone who cares to react
/// (docs/patterns/choreography.md - today that's the Valuation Service; tomorrow a charting read
/// model or a limit monitor, with no change to the emitter).
/// </summary>
/// <remarks>
/// Delivery is at-least-once (see <c>Benzene.Kafka.Streaming</c>'s checkpoint contract, and the
/// Market-Data Aggregator's own note on in-memory rolling state), so the same bar can legitimately
/// arrive twice. <c>(Symbol, WindowStart)</c> is its natural idempotency key: a consumer should
/// upsert on it rather than accumulate.
/// </remarks>
public class BarClosed
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The first tick's price in the window.</summary>
    public decimal Open { get; set; }

    /// <summary>The highest tick price in the window.</summary>
    public decimal High { get; set; }

    /// <summary>The lowest tick price in the window.</summary>
    public decimal Low { get; set; }

    /// <summary>The last tick's price in the window - the mark every downstream valuation uses.</summary>
    public decimal Close { get; set; }

    /// <summary>The total traded size across the window.</summary>
    public decimal Volume { get; set; }

    /// <summary>The (inclusive) start of the window this bar covers.</summary>
    public DateTimeOffset WindowStart { get; set; }

    /// <summary>The (exclusive) end of the window this bar covers.</summary>
    public DateTimeOffset WindowEnd { get; set; }

    /// <summary>How many ticks the bar was built from - useful when eyeballing the demo.</summary>
    public int TickCount { get; set; }
}
