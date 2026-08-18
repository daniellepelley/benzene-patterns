using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.Streaming.TickPipeline;

// ── Producing ───────────────────────────────────────────────────────────────────────────────────

public class PublishTicksRequest
{
    public List<TickInput> Ticks { get; set; } = new();
}

public class TickInput
{
    /// <summary>The producer's partition key. Setting it to the symbol is what keeps a symbol ordered.</summary>
    public string Symbol { get; set; } = string.Empty;

    public decimal Price { get; set; }
    public long Size { get; set; }

    /// <summary>The bar this tick belongs to, e.g. <c>2026-08-19T09:31</c>.</summary>
    public string Minute { get; set; } = string.Empty;
}

public class PublishedResponse
{
    public int Published { get; set; }
    public long FirstSequence { get; set; }
    public long LastSequence { get; set; }
}

/// <summary>Appends ticks to the shard. The producer half, in one endpoint.</summary>
[Message("ticks:publish")]
[HttpEndpoint("POST", "/ticks")]
public class PublishTicksHandler : IMessageHandler<PublishTicksRequest, PublishedResponse>
{
    private readonly Shard _shard;

    public PublishTicksHandler(Shard shard)
    {
        _shard = shard;
    }

    public Task<IBenzeneResult<PublishedResponse>> HandleAsync(PublishTicksRequest request)
    {
        if (request.Ticks.Count == 0)
        {
            return BenzeneResult.ValidationError<PublishedResponse>("At least one tick is required.").AsTask();
        }

        long first = 0, last = 0;
        foreach (var input in request.Ticks)
        {
            var tick = _shard.Append(input.Symbol, input.Price, input.Size, input.Minute);
            if (first == 0)
            {
                first = tick.SequenceNumber;
            }

            last = tick.SequenceNumber;
        }

        return BenzeneResult.Ok(new PublishedResponse
        {
            Published = request.Ticks.Count,
            FirstSequence = first,
            LastSequence = last
        }).AsTask();
    }
}

// ── Consuming ───────────────────────────────────────────────────────────────────────────────────

public class DrainRequest
{
    /// <summary>The batch size this invocation is handed.</summary>
    public int MaxRecords { get; set; } = 100;

    public int WindowSize { get; set; } = 3;

    /// <summary><c>window</c> (default) or <c>partition</c>. See <see cref="StreamMode"/>.</summary>
    public string Mode { get; set; } = "window";

    /// <summary>Fold with `+=` instead of the idempotent upsert — the bug, on demand.</summary>
    public bool Naive { get; set; }
}

public class DrainResponse
{
    public int RecordsRead { get; set; }
    public int RecordsApplied { get; set; }
    public long CheckpointBefore { get; set; }
    public long CheckpointAfter { get; set; }
    public bool Failed { get; set; }
    public string? FailureReason { get; set; }
}

/// <summary>
/// Runs exactly one invocation: read a batch from the checkpoint forward, run the pipeline once.
/// </summary>
/// <remarks>
/// Driving this from an endpoint rather than a poll loop is what makes the checkpoint observable one
/// batch at a time — which is the only way to show "resume from the failure, not from zero" as
/// something other than a claim.
/// </remarks>
[Message("stream:drain")]
[HttpEndpoint("POST", "/drain")]
public class DrainHandler : IMessageHandler<DrainRequest, DrainResponse>
{
    private readonly StreamProcessor _processor;

    public DrainHandler(StreamProcessor processor)
    {
        _processor = processor;
    }

    public async Task<IBenzeneResult<DrainResponse>> HandleAsync(DrainRequest request)
    {
        if (!Enum.TryParse<StreamMode>(request.Mode, ignoreCase: true, out var mode))
        {
            return BenzeneResult.ValidationError<DrainResponse>(
                $"Unknown mode '{request.Mode}'. Use 'window' or 'partition'.");
        }

        var result = await _processor.DrainAsync(new StreamOptions
        {
            MaxRecords = request.MaxRecords,
            WindowSize = request.WindowSize,
            Mode = mode,
            Naive = request.Naive
        });

        return BenzeneResult.Ok(new DrainResponse
        {
            RecordsRead = result.RecordsRead,
            RecordsApplied = result.RecordsApplied,
            CheckpointBefore = result.CheckpointBefore,
            CheckpointAfter = result.CheckpointAfter,
            Failed = result.Failed,
            FailureReason = result.FailureReason
        });
    }
}

// ── Inspecting ──────────────────────────────────────────────────────────────────────────────────

public class ShardStatus
{
    public int Records { get; set; }
    public long LastSequence { get; set; }
    public long Checkpoint { get; set; }
    public long Lag { get; set; }
}

[Message("shard:status")]
[HttpEndpoint("GET", "/shard")]
public class ShardStatusHandler : IMessageHandler<Void, ShardStatus>
{
    private readonly Shard _shard;

    public ShardStatusHandler(Shard shard)
    {
        _shard = shard;
    }

    public Task<IBenzeneResult<ShardStatus>> HandleAsync(Void request)
        => BenzeneResult.Ok(new ShardStatus
        {
            Records = _shard.Count,
            LastSequence = _shard.LastSequence,
            Checkpoint = _shard.Checkpoint,
            Lag = _shard.LastSequence - _shard.Checkpoint
        }).AsTask();
}

public class BarsResponse
{
    public int Count { get; set; }
    public List<Bar> Bars { get; set; } = new();
}

[Message("bars:list")]
[HttpEndpoint("GET", "/bars")]
public class ListBarsHandler : IMessageHandler<Void, BarsResponse>
{
    private readonly BarStore _bars;

    public ListBarsHandler(BarStore bars)
    {
        _bars = bars;
    }

    public Task<IBenzeneResult<BarsResponse>> HandleAsync(Void request)
    {
        var all = _bars.All();
        return BenzeneResult.Ok(new BarsResponse { Count = all.Count, Bars = all.ToList() }).AsTask();
    }
}

// ── Rigging a failure ───────────────────────────────────────────────────────────────────────────

public class PoisonRequest
{
    public long SequenceNumber { get; set; }

    /// <summary>How many times that record should fail before it succeeds.</summary>
    public int Times { get; set; } = 1;
}

/// <summary>
/// Marks a record to fail — the demo's stand-in for the thing that eventually happens on its own.
/// </summary>
[Message("stream:poison")]
[HttpEndpoint("POST", "/poison")]
public class PoisonHandler : IMessageHandler<PoisonRequest, PoisonResponse>
{
    private readonly PoisonPills _pills;

    public PoisonHandler(PoisonPills pills)
    {
        _pills = pills;
    }

    public Task<IBenzeneResult<PoisonResponse>> HandleAsync(PoisonRequest request)
    {
        _pills.Add(request.SequenceNumber, request.Times);
        return BenzeneResult.Ok(new PoisonResponse
        {
            SequenceNumber = request.SequenceNumber,
            Times = request.Times
        }).AsTask();
    }
}

public class PoisonResponse
{
    public long SequenceNumber { get; set; }
    public int Times { get; set; }
}

/// <summary>Clears the bars so a scenario can be re-run from the same shard.</summary>
/// <remarks>
/// The SHARD is not cleared — it cannot be, that is what a log is. Clearing the derived bars and
/// re-draining is the streaming equivalent of the read model's rebuild.
/// </remarks>
[Message("bars:clear")]
[HttpEndpoint("POST", "/bars/clear")]
public class ClearBarsHandler : IMessageHandler<Void, BarsResponse>
{
    private readonly BarStore _bars;
    private readonly PoisonPills _pills;

    public ClearBarsHandler(BarStore bars, PoisonPills pills)
    {
        _bars = bars;
        _pills = pills;
    }

    public Task<IBenzeneResult<BarsResponse>> HandleAsync(Void request)
    {
        _bars.Clear();
        _pills.Clear();
        return BenzeneResult.Ok(new BarsResponse()).AsTask();
    }
}
