using Benzene.Patterns.RealTimeRisk.Contracts;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator;

/// <summary>
/// The rolling OHLC state: one open bar per symbol, folded from ticks as they arrive, closed and
/// handed back when the symbol's window rolls over (or when wall-clock time says the window has
/// ended). Pure, transport-free logic - <see cref="OhlcAggregationMiddleware"/> is what feeds it off
/// the Kafka stream, and this class is unit-tested on its own.
/// </summary>
/// <remarks>
/// <para><strong>Why in-memory rolling state is genuinely correct here, and not a simplification
/// with an asterisk.</strong> docs/patterns/streaming-processing.md is emphatic that "rolling state
/// across invocations lives in a store, not in memory" - and it is right, for the Kinesis/Lambda
/// shape it works through: each Lambda invocation is a fresh, stateless process that sees exactly
/// one batch, so a bar spanning several batches has nowhere to live but an external
/// per-(symbol, minute) item.</para>
/// <para><c>BenzeneKafkaStreamWorker</c> is a different animal. It is one continuously-running
/// process that consumes batch after batch in the same address space, so a <c>Dictionary</c> keyed
/// by symbol <em>is</em> valid rolling state across batches - the store in the doc's example exists
/// to survive the invocation boundary, and there is no invocation boundary here. The condition it
/// rests on is exactly one instance owning the state, which this single-instance local Compose demo
/// satisfies by construction.</para>
/// <para>What it costs, stated plainly: (a) scaling the aggregator to more than one instance breaks
/// it unless each instance owns whole partitions <em>and</em> a symbol never moves between them - a
/// rebalance would migrate a symbol mid-window and split its bar; (b) a restart loses whichever
/// windows were open, so at most one window per symbol is lost or partial. Both are properties of
/// choosing in-memory state, not bugs; the fix for either is the doc's external store, at which
/// point the Kinesis guidance applies verbatim.</para>
/// </remarks>
public sealed class OhlcBarAggregator
{
    private sealed class OpenBar
    {
        public required DateTimeOffset WindowStart { get; init; }
        public decimal Open { get; set; }
        public decimal High { get; set; }
        public decimal Low { get; set; }
        public decimal Close { get; set; }
        public decimal Volume { get; set; }
        public int TickCount { get; set; }
    }

    private readonly Dictionary<string, OpenBar> _openBars = new(StringComparer.Ordinal);
    // The last window already published per symbol, so a tick arriving after CloseCompleted() has
    // flushed that symbol's bar is treated as late rather than silently reopening a published window
    // as a brand-new bar.
    private readonly Dictionary<string, DateTimeOffset> _lastClosedWindow = new(StringComparer.Ordinal);
    private readonly TimeSpan _barInterval;

    /// <param name="barInterval">
    /// The bar width. One minute is the pattern's worked example (and this service's default); it is
    /// configurable only so the local demo and CI don't have to wait a full minute for the first bar
    /// to appear - see <c>StartUp</c> and docker-compose.yml.
    /// </param>
    public OhlcBarAggregator(TimeSpan barInterval)
    {
        if (barInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(barInterval), barInterval, "The bar interval must be greater than zero.");
        }

        _barInterval = barInterval;
    }

    /// <summary>The number of ticks dropped for arriving after their window had already closed.</summary>
    public int LateTickCount { get; private set; }

    /// <summary>
    /// Folds one tick into its symbol's bar. Returns the previous bar if this tick started a new
    /// window for the symbol (the previous window is complete the moment a later one is observed),
    /// otherwise <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A tick belonging to a window <em>older</em> than the symbol's currently open one is dropped
    /// and counted in <see cref="LateTickCount"/>: its bar has already been published, and silently
    /// mutating a published bar would be worse than losing a stale print. Kafka keys ticks by symbol
    /// so one symbol's ticks share a partition and stay in order, which makes this the rare case it
    /// should be - a genuinely out-of-order feed would need watermarking and bar revisions, which is
    /// a different design, not a tweak to this one.
    /// </remarks>
    public BarClosed? Add(MarketTick tick)
    {
        var windowStart = WindowStartFor(tick.Timestamp, _barInterval);

        if (!_openBars.TryGetValue(tick.Symbol, out var open))
        {
            if (_lastClosedWindow.TryGetValue(tick.Symbol, out var lastClosed) && windowStart <= lastClosed)
            {
                LateTickCount++;
                return null;
            }

            _openBars[tick.Symbol] = NewBar(windowStart, tick);
            return null;
        }

        if (windowStart < open.WindowStart)
        {
            LateTickCount++;
            return null;
        }

        if (windowStart > open.WindowStart)
        {
            var closed = Close(tick.Symbol, open);
            _openBars[tick.Symbol] = NewBar(windowStart, tick);
            return closed;
        }

        open.High = Math.Max(open.High, tick.Price);
        open.Low = Math.Min(open.Low, tick.Price);
        open.Close = tick.Price;
        open.Volume += tick.Size;
        open.TickCount++;
        return null;
    }

    /// <summary>
    /// Closes every bar whose window has already ended as of <paramref name="now"/>, so a symbol
    /// that stops ticking still publishes its final bar instead of holding it open forever. Returns
    /// the closed bars, oldest window first.
    /// </summary>
    public IReadOnlyList<BarClosed> CloseCompleted(DateTimeOffset now)
    {
        List<BarClosed>? closed = null;

        foreach (var symbol in _openBars.Keys.ToList())
        {
            var open = _openBars[symbol];
            if (open.WindowStart + _barInterval > now)
            {
                continue;
            }

            (closed ??= new List<BarClosed>()).Add(Close(symbol, open));
            _openBars.Remove(symbol);
        }

        closed?.Sort((left, right) => left.WindowStart.CompareTo(right.WindowStart));
        return (IReadOnlyList<BarClosed>?)closed ?? Array.Empty<BarClosed>();
    }

    /// <summary>
    /// Floors <paramref name="timestamp"/> to the start of the window it falls in, measured from the
    /// Unix epoch in UTC so every instance and every symbol agree on the boundaries.
    /// </summary>
    public static DateTimeOffset WindowStartFor(DateTimeOffset timestamp, TimeSpan interval)
    {
        var ticksSinceEpoch = timestamp.ToUniversalTime().UtcDateTime.Ticks - DateTime.UnixEpoch.Ticks;
        var flooredTicks = ticksSinceEpoch - ticksSinceEpoch % interval.Ticks;
        return new DateTimeOffset(DateTime.UnixEpoch.AddTicks(flooredTicks), TimeSpan.Zero);
    }

    private static OpenBar NewBar(DateTimeOffset windowStart, MarketTick tick) => new()
    {
        WindowStart = windowStart,
        Open = tick.Price,
        High = tick.Price,
        Low = tick.Price,
        Close = tick.Price,
        Volume = tick.Size,
        TickCount = 1
    };

    private BarClosed Close(string symbol, OpenBar open)
    {
        _lastClosedWindow[symbol] = open.WindowStart;
        return new BarClosed
        {
            Symbol = symbol,
            Open = open.Open,
            High = open.High,
            Low = open.Low,
            Close = open.Close,
            Volume = open.Volume,
            WindowStart = open.WindowStart,
            WindowEnd = open.WindowStart + _barInterval,
            TickCount = open.TickCount
        };
    }
}
