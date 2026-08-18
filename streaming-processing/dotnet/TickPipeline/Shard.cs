using System.Collections.Concurrent;

namespace Benzene.Patterns.Streaming.TickPipeline;

/// <summary>One market-data tick, as it sits on a shard.</summary>
public class Tick
{
    /// <summary>The shard-wide position. Monotonic, gap-free, and the thing a checkpoint names.</summary>
    public long SequenceNumber { get; set; }

    /// <summary>The producer's partition key. Set to the symbol, so a symbol's ticks stay ordered.</summary>
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }
    public long Size { get; set; }

    /// <summary>The minute this tick belongs to, as a bar key (e.g. <c>2026-08-19T09:31</c>).</summary>
    public string Minute { get; set; } = string.Empty;
}

/// <summary>
/// A local stand-in for one Kinesis shard: an append-only, ordered, replayable log with a
/// checkpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one part of the example that is not the real transport, and it is deliberately the
/// smallest part.</b> Everything the pattern is actually about — the fan-in
/// <c>StreamContext</c>, <c>UseStream</c>, <c>PartitionBy</c>, <c>Window</c>, the checkpointer
/// interface and the rolling store — is Benzene's own, from <c>Benzene.Core.Middleware</c>, and works
/// identically under <c>UseKinesisStream</c> or <c>UseEventHubStream</c>. What a shard has to provide
/// is exactly three properties, and this provides all three:
/// </para>
/// <list type="number">
/// <item>records are <b>ordered</b> and addressed by a monotonic sequence number;</item>
/// <item>a batch is read from the checkpoint <b>forward</b>, so a failure replays from the failure;</item>
/// <item>the checkpoint <b>only ever advances</b>.</item>
/// </list>
/// <para>
/// Which means the interesting failure — at-least-once redelivery of a mid-batch record — is
/// reproducible here on demand, instead of being something you read about and hope you handled.
/// </para>
/// </remarks>
public class Shard
{
    private readonly ConcurrentQueue<Tick> _records = new();
    private long _sequence;
    private long _checkpoint;

    /// <summary>The last sequence number acknowledged as processed. Never goes backwards.</summary>
    public long Checkpoint => Interlocked.Read(ref _checkpoint);

    public long LastSequence => Interlocked.Read(ref _sequence);

    public int Count => _records.Count;

    public Tick Append(string symbol, decimal price, long size, string minute)
    {
        var tick = new Tick
        {
            SequenceNumber = Interlocked.Increment(ref _sequence),
            Symbol = symbol,
            Price = price,
            Size = size,
            Minute = minute
        };

        _records.Enqueue(tick);
        return tick;
    }

    /// <summary>Reads up to <paramref name="maxRecords"/> records after the checkpoint, in order.</summary>
    public IReadOnlyList<Tick> ReadBatch(int maxRecords)
        => _records
            .Where(x => x.SequenceNumber > Checkpoint)
            .OrderBy(x => x.SequenceNumber)
            .Take(maxRecords)
            .ToList();

    /// <summary>
    /// Advances the checkpoint, never backwards.
    /// </summary>
    /// <remarks>
    /// The monotonic guarantee is not a nicety. A checkpoint that could move backwards would replay
    /// records that were already acknowledged, and the only thing standing between that and wrong
    /// numbers is whether the aggregation happens to be idempotent — which is a bet, not a design.
    /// </remarks>
    public void CheckpointTo(long sequenceNumber)
    {
        long current;
        do
        {
            current = Interlocked.Read(ref _checkpoint);
            if (sequenceNumber <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref _checkpoint, sequenceNumber, current) != current);
    }
}
