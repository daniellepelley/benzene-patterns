using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Core.Middleware;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Streaming.TickPipeline;

/// <summary>How the batch is walked, and therefore how progress is checkpointed.</summary>
public enum StreamMode
{
    /// <summary>
    /// <c>Window(n)</c>: lazy, shard-ordered, checkpoint after each window.
    /// </summary>
    /// <remarks>
    /// Partial progress survives a failure — only the window that failed replays. This is the shape
    /// to reach for by default: it batches store round-trips without giving up either laziness or
    /// ordered progress.
    /// </remarks>
    Window,

    /// <summary>
    /// <c>PartitionBy(symbol)</c>: the whole batch grouped by key, checkpoint once at the end.
    /// </summary>
    /// <remarks>
    /// The shape the pattern doc shows, and the right one when the computation genuinely needs all of
    /// a key's records in the batch together. It costs two things, and both are worth knowing before
    /// choosing it: the operator <b>buffers the whole batch</b> to group it, and grouping destroys
    /// shard order, so there is no meaningful mid-batch checkpoint — a failure replays everything.
    /// </remarks>
    Partition
}

/// <summary>The outcome of running one batch through the pipeline.</summary>
public record DrainResult(
    int RecordsRead,
    int RecordsApplied,
    long CheckpointBefore,
    long CheckpointAfter,
    bool Failed,
    string? FailureReason);

/// <summary>
/// Reads one batch from the shard and runs it through a real Benzene stream pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>One invocation, one batch, one pipeline run.</b> That is the fan-IN model, and it is the whole
/// reason to reach for the stream binding: a fan-out transport would turn a batch of N records into
/// N independent pipeline invocations processed concurrently, which throws away the two things this
/// pipeline needs — order, and the ability to aggregate across records.
/// </para>
/// <para>
/// The <see cref="StreamContext{TItem}"/>, the <c>UseStream</c> step, <c>Window</c>,
/// <c>PartitionBy</c> and <see cref="IStreamCheckpointer{TItem}"/> are all Benzene's own, from
/// <c>Benzene.Core.Middleware</c>. Under <c>UseKinesisStream</c> or <c>UseEventHubStream</c> the
/// handler body below is unchanged; what changes is who hands it the batch.
/// </para>
/// </remarks>
public class StreamProcessor
{
    private readonly Shard _shard;
    private readonly BarStore _bars;
    private readonly PoisonPills _poison;
    private readonly IMiddlewarePipeline<StreamContext<Tick>> _pipeline;
    private readonly IServiceResolver _resolver;
    private readonly ILogger<StreamProcessor> _logger;

    public StreamProcessor(Shard shard, BarStore bars, PoisonPills poison,
        IMiddlewarePipeline<StreamContext<Tick>> pipeline, IServiceResolver resolver,
        ILogger<StreamProcessor> logger)
    {
        _shard = shard;
        _bars = bars;
        _poison = poison;
        _pipeline = pipeline;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task<DrainResult> DrainAsync(StreamOptions options)
    {
        var before = _shard.Checkpoint;
        var batch = _shard.ReadBatch(options.MaxRecords);
        if (batch.Count == 0)
        {
            return new DrainResult(0, 0, before, before, false, null);
        }

        var run = new RunState(options);
        string? failure = null;

        // Per-run state rides on Metadata, which is what it is for - the pipeline itself is built
        // once at start-up and shared, exactly as a transport binding's would be.
        var context = new StreamContext<Tick>(
            Pull(batch),
            new ShardCheckpointer(_shard),
            CancellationToken.None,
            new Dictionary<string, object> { ["shard"] = "shard-000", ["run"] = run });

        try
        {
            await _pipeline.HandleAsync(context, _resolver);
        }
        catch (PoisonRecordException ex)
        {
            // What a real stream transport does with a thrown batch: the checkpoint stays where it
            // was, and the source re-presents from there. Nothing is lost; some records are
            // re-presented, which is the deal.
            failure = ex.Message;
            _logger.LogWarning("Batch failed at sequence {Sequence}; checkpoint stays at {Checkpoint}",
                ex.SequenceNumber, _shard.Checkpoint);
        }

        return new DrainResult(batch.Count, run.Applied, before, _shard.Checkpoint, failure != null, failure);

        // Local so the counters above are in scope. The batch is walked lazily - the pull IS the
        // backpressure, and nothing here materializes the whole shard.
        async IAsyncEnumerable<Tick> Pull(IReadOnlyList<Tick> records)
        {
            foreach (var record in records)
            {
                yield return record;
                await Task.Yield();
            }
        }
    }

    /// <summary>Builds the stream pipeline. Called once, at start-up.</summary>
    /// <remarks>
    /// A <c>UseStream</c> step is ordinary middleware, so it composes with everything else on the
    /// same builder — correlation, metrics, exception handling — rather than being a parallel world
    /// with its own conventions.
    /// </remarks>
    public static IMiddlewarePipeline<StreamContext<Tick>> BuildPipeline(
        IBenzeneServiceContainer container, BarStore store, PoisonPills pills)
    {
        var builder = new MiddlewarePipelineBuilder<StreamContext<Tick>>(container);

        // UseTerminalStream, not the shipped UseStream - see TerminalStream.cs for the one-word
        // framework fix it stands in for. The step's body is exactly what UseStream would run.
        builder.UseTerminalStream<Tick>(async (context) =>
        {
            var run = (RunState)context.Metadata["run"];
            var o = run.Options;
            var ct = context.CancellationToken;

            if (o.Mode == StreamMode.Partition)
            {
                // PartitionBy buffers the whole batch to group it, and the groups come out in
                // first-seen key order rather than shard order - so there is no sensible mid-batch
                // checkpoint. One checkpoint, at the end, over the whole batch.
                Tick? last = null;
                await foreach (var group in context.Items.PartitionBy(x => x.Symbol, ct))
                {
                    foreach (var tick in group.Value)
                    {
                        pills.ThrowIfPoisoned(tick);
                        Fold(store, tick, o.Naive);
                        run.Applied++;
                        last = tick;
                    }
                }

                if (last != null)
                {
                    await context.Checkpointer.CheckpointAsync(last);
                }

                return;
            }

            // Window is the lazy one: it yields fixed-size windows as they fill, without holding the
            // batch. Shard order is preserved, so the last record of a window is a meaningful
            // checkpoint - and a failure replays only the window it happened in.
            await foreach (var window in context.Items.Window(o.WindowSize, ct))
            {
                foreach (var tick in window)
                {
                    pills.ThrowIfPoisoned(tick);
                    Fold(store, tick, o.Naive);
                    run.Applied++;
                }

                await context.Checkpointer.CheckpointAsync(window[^1]);
            }
        });

        return builder.Build();
    }

    private static void Fold(BarStore store, Tick tick, bool naive)
    {
        if (naive)
        {
            store.ApplyNaive(tick);
        }
        else
        {
            store.Apply(tick);
        }
    }
}

/// <summary>Per-run options, read by the pipeline step through a resolver so one pipeline serves all runs.</summary>
public class StreamOptions
{
    /// <summary>How many records this invocation is handed - a stream binding's batch size.</summary>
    public int MaxRecords { get; set; } = 100;

    public int WindowSize { get; set; } = 3;
    public StreamMode Mode { get; set; } = StreamMode.Window;

    /// <summary>Fold with the obvious, wrong `+=` instead of the idempotent upsert.</summary>
    public bool Naive { get; set; }
}

/// <summary>What one invocation carries: its options, and what it did.</summary>
internal class RunState
{
    public RunState(StreamOptions options)
    {
        Options = options;
    }

    public StreamOptions Options { get; }

    /// <summary>Records folded before the batch ended - normally or by failing.</summary>
    public int Applied { get; set; }
}

/// <summary>Acknowledges progress to the shard. The transport hook, implemented for a local shard.</summary>
/// <remarks>
/// The interface is <see cref="IStreamCheckpointer{TItem}"/>, unchanged: under Kinesis this is the
/// binding's own checkpointer and this file does not exist. It is here because a checkpoint that
/// nothing implements cannot be shown to advance.
/// </remarks>
public class ShardCheckpointer : IStreamCheckpointer<Tick>
{
    private readonly Shard _shard;

    public ShardCheckpointer(Shard shard)
    {
        _shard = shard;
    }

    public Task CheckpointAsync(Tick lastProcessed)
    {
        _shard.CheckpointTo(lastProcessed.SequenceNumber);
        return Task.CompletedTask;
    }
}

/// <summary>Sequence numbers rigged to fail, and how many more times each should.</summary>
/// <remarks>
/// A demo affordance with a real job: at-least-once redelivery is the condition under which a
/// rolling aggregation is either right or quietly wrong, and it is not something you can wait for.
/// </remarks>
public class PoisonPills
{
    private readonly Dictionary<long, int> _remaining = new();

    public void Add(long sequenceNumber, int times)
    {
        lock (_remaining)
        {
            _remaining[sequenceNumber] = times;
        }
    }

    public void ThrowIfPoisoned(Tick tick)
    {
        lock (_remaining)
        {
            if (!_remaining.TryGetValue(tick.SequenceNumber, out var left) || left <= 0)
            {
                return;
            }

            _remaining[tick.SequenceNumber] = left - 1;
            throw new PoisonRecordException(tick.SequenceNumber);
        }
    }

    public void Clear()
    {
        lock (_remaining)
        {
            _remaining.Clear();
        }
    }
}

public class PoisonRecordException : Exception
{
    public PoisonRecordException(long sequenceNumber)
        : base($"Record at sequence {sequenceNumber} failed to process.")
    {
        SequenceNumber = sequenceNumber;
    }

    public long SequenceNumber { get; }
}
