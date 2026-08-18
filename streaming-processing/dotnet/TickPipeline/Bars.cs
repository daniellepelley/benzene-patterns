using System.Collections.Concurrent;

namespace Benzene.Patterns.Streaming.TickPipeline;

/// <summary>An OHLC bar: one symbol, one minute.</summary>
public class Bar
{
    public string Symbol { get; set; } = string.Empty;
    public string Minute { get; set; } = string.Empty;
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }

    /// <summary>The ticks this bar was folded from, by sequence number.</summary>
    /// <remarks>
    /// Kept because it is what makes the idempotent fold possible AND checkable: a redelivered tick
    /// is recognised by its sequence number, and a reader can see exactly which ticks a bar is made
    /// of rather than trusting a total.
    /// </remarks>
    public List<long> Sequences { get; set; } = new();
}

/// <summary>
/// The rolling state that lives ACROSS invocations. The single thing newcomers get wrong.
/// </summary>
/// <remarks>
/// <para>
/// One stream invocation sees one batch. A one-minute bar routinely spans several batches, so it
/// cannot live in memory between them — it lives here, keyed by <c>(symbol, minute)</c>.
/// <c>Window</c> and <c>PartitionBy</c> order and group <b>within</b> a batch; this store is what
/// carries a bar from one batch to the next.
/// </para>
/// <para>
/// In a real deployment this is DynamoDB or Redis, one item per <c>(symbol, minute)</c>. In-process
/// here because the point being made is about the boundary, not the database.
/// </para>
/// </remarks>
public class BarStore
{
    private readonly ConcurrentDictionary<(string Symbol, string Minute), Bar> _bars = new();

    /// <summary>
    /// Folds a tick into its bar — idempotently, by sequence number.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This method is the whole point of the example.</b> Delivery is at-least-once with
    /// resume-from-first-failed-sequence, so a tick WILL be presented twice; the aggregation has to
    /// converge anyway. Recognising the sequence number and returning is what makes that true.
    /// </para>
    /// <para>
    /// Compare <see cref="ApplyNaive"/>, which does the obvious thing — <c>Volume += tick.Size</c> —
    /// and is wrong in a way that nothing reports. Both produce a bar; only one produces the right
    /// one after a replay.
    /// </para>
    /// </remarks>
    public void Apply(Tick tick)
    {
        _bars.AddOrUpdate((tick.Symbol, tick.Minute),
            _ => NewBar(tick),
            (_, bar) =>
            {
                lock (bar)
                {
                    // Already folded in. A replay must be a no-op, not a second contribution.
                    if (bar.Sequences.Contains(tick.SequenceNumber))
                    {
                        return bar;
                    }

                    bar.High = Math.Max(bar.High, tick.Price);
                    bar.Low = Math.Min(bar.Low, tick.Price);
                    bar.Close = tick.Price;
                    bar.Volume += tick.Size;
                    bar.Sequences.Add(tick.SequenceNumber);
                    return bar;
                }
            });
    }

    /// <summary>
    /// The same fold without the idempotency check — the bug, kept runnable so it can be shown.
    /// </summary>
    /// <remarks>
    /// Reads as obviously correct, passes every test that never replays a record, and inflates volume
    /// the first time a batch resumes from a mid-batch failure. The example exposes it behind a flag
    /// so the difference is a number on screen rather than a warning in a document.
    /// </remarks>
    public void ApplyNaive(Tick tick)
    {
        _bars.AddOrUpdate((tick.Symbol, tick.Minute),
            _ => NewBar(tick),
            (_, bar) =>
            {
                lock (bar)
                {
                    bar.High = Math.Max(bar.High, tick.Price);
                    bar.Low = Math.Min(bar.Low, tick.Price);
                    bar.Close = tick.Price;
                    bar.Volume += tick.Size;
                    bar.Sequences.Add(tick.SequenceNumber);
                    return bar;
                }
            });
    }

    public IReadOnlyList<Bar> All()
        => _bars.Values
            .OrderBy(x => x.Symbol, StringComparer.Ordinal)
            .ThenBy(x => x.Minute, StringComparer.Ordinal)
            .ToList();

    public Bar? Get(string symbol, string minute)
        => _bars.TryGetValue((symbol, minute), out var bar) ? bar : null;

    public void Clear() => _bars.Clear();

    private static Bar NewBar(Tick tick) => new()
    {
        Symbol = tick.Symbol,
        Minute = tick.Minute,
        Open = tick.Price,
        High = tick.Price,
        Low = tick.Price,
        Close = tick.Price,
        Volume = tick.Size,
        Sequences = new List<long> { tick.SequenceNumber }
    };
}
