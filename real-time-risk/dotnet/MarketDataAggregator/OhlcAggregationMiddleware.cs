using System.Text.Json;
using Benzene.Abstractions.Middleware;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
// Benzene.Clients also exposes a JsonSerializer; this file means System.Text.Json's.
using JsonSerializer = System.Text.Json.JsonSerializer;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator;

/// <summary>
/// The stream handler: one Kafka batch in, zero or more <see cref="Topics.BarClosed"/> events out.
/// This is docs/patterns/streaming-processing.md's worked example, on the Kafka binding instead of
/// the Kinesis one - "the same handlers, different host".
/// </summary>
/// <remarks>
/// <para>Written as an <see cref="IMiddleware{TContext}"/> rather than an inline
/// <c>UseStream(context =&gt; ...)</c> lambda for one reason: the lambda overloads close over
/// nothing but the context, and this handler needs <see cref="IBenzeneMessageSender"/> resolved from
/// the batch's own DI scope. The <c>Use(resolver =&gt; ...)</c> factory gives exactly that, and the
/// body is otherwise the doc's example verbatim.</para>
/// <para>Batch shape and ordering come from <c>PartitionBy(r =&gt; r.TopicPartition)</c>, per the
/// doc's guidance to "partition on the record's partition key to restore the per-shard grouping the
/// poller flattened". Ticks are keyed by symbol on the producer side, so Kafka's own partitioner
/// already keeps one symbol on one partition - no custom partitioner, and no need to decode a record
/// to know how to group it.</para>
/// </remarks>
public class OhlcAggregationMiddleware : IMiddleware<StreamContext<ConsumeResult<string, string>>>
{
    private static readonly JsonSerializerOptions TickJson = new(JsonSerializerOptions.Web);

    private readonly OhlcBarAggregator _aggregator;
    private readonly StreamReplayGuard _replayGuard;
    private readonly IBenzeneMessageSender _sender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OhlcAggregationMiddleware> _logger;

    public OhlcAggregationMiddleware(
        OhlcBarAggregator aggregator,
        StreamReplayGuard replayGuard,
        IBenzeneMessageSender sender,
        TimeProvider timeProvider,
        ILogger<OhlcAggregationMiddleware> logger)
    {
        _aggregator = aggregator;
        _replayGuard = replayGuard;
        _sender = sender;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public string Name => nameof(OhlcAggregationMiddleware);

    public async Task HandleAsync(StreamContext<ConsumeResult<string, string>> context, Func<Task> next)
    {
        await foreach (var partition in context.Items.PartitionBy(r => r.TopicPartition, context.CancellationToken))
        {
            var streamKey = partition.Key.ToString();
            var records = partition.Value;

            foreach (var record in records)
            {
                await ApplyAsync(streamKey, record);
            }

            // Checkpoint this partition's frontier, not merely the last record we happened to touch.
            // PartitionBy hands the partition's records back in offset order, and every one of them
            // has now been folded in (or skipped as a replay/undecodable record, which is equally
            // "done"), so the last record IS the frontier - which is exactly the contract
            // KafkaStreamCheckpointer documents, given a committed offset is a watermark with no gap
            // tracking. Doing it here rather than leaning on AutoCheckpointOnSuccess also means a
            // later partition throwing can't undo an earlier one's genuine progress.
            await context.Checkpointer.CheckpointAsync(records[^1]);
        }

        // Close any window that has ended in wall-clock terms but whose symbol hasn't ticked into the
        // next one yet, so a bar doesn't wait on the next print to be published. This runs at
        // batch end, so it depends on the topic seeing *some* traffic: a total feed outage leaves the
        // last window open until traffic resumes (BenzeneKafkaStreamWorker never runs the pipeline
        // for an empty batch). A production aggregator would want a punctuation timer for that; for a
        // continuously-ticking demo feed it is a non-event, and inventing a timer here would be
        // machinery the pattern doesn't need to show.
        foreach (var bar in _aggregator.CloseCompleted(_timeProvider.GetUtcNow()))
        {
            await PublishAsync(bar);
        }

        await next();
    }

    private async Task ApplyAsync(string streamKey, ConsumeResult<string, string> record)
    {
        var offset = record.Offset.Value;

        if (!_replayGuard.ShouldApply(streamKey, offset))
        {
            return;
        }

        var tick = Deserialize(record);
        if (tick is not null)
        {
            var closed = _aggregator.Add(tick);
            if (closed is not null)
            {
                await PublishAsync(closed);
            }
        }

        _replayGuard.MarkApplied(streamKey, offset);
    }

    private MarketTick? Deserialize(ConsumeResult<string, string> record)
    {
        try
        {
            var tick = JsonSerializer.Deserialize<MarketTick>(record.Message.Value, TickJson);
            if (tick is null || string.IsNullOrWhiteSpace(tick.Symbol))
            {
                _logger.LogWarning("Skipping a tick with no symbol at {TopicPartitionOffset}", record.TopicPartitionOffset);
                return null;
            }

            return tick;
        }
        catch (JsonException ex)
        {
            // Skipped, not thrown: an undecodable record is poison, and throwing would seek the
            // partition back to it and retry it forever (the streaming binding's default failure
            // policy). Everything that *can* fail transiently - the produce below - still throws.
            _logger.LogWarning(ex, "Skipping an undecodable tick at {TopicPartitionOffset}", record.TopicPartitionOffset);
            return null;
        }
    }

    private async Task PublishAsync(BarClosed bar)
    {
        // Fire-and-forget choreography: a Void response type means nobody is waited on, and this
        // service has no idea the Valuation Service exists. The "symbol" header becomes the Kafka
        // message key (see OutboundKafkaContextConverter), so bar-closed is partitioned by symbol
        // exactly as the tick feed is.
        var result = await _sender.SendAsync<BarClosed, Void>(
            Topics.BarClosed, bar, new Dictionary<string, string> { ["symbol"] = bar.Symbol });

        if (!result.IsSuccessful)
        {
            throw new InvalidOperationException(
                $"Publishing {Topics.BarClosed} for {bar.Symbol}@{bar.WindowStart:O} failed with status {result.Status}: "
                + string.Join("; ", result.Errors));
        }

        _logger.LogInformation(
            "Closed {Symbol} {WindowStart:HH:mm:ss}-{WindowEnd:HH:mm:ss}: O={Open} H={High} L={Low} C={Close} V={Volume} ({TickCount} ticks)",
            bar.Symbol, bar.WindowStart, bar.WindowEnd, bar.Open, bar.High, bar.Low, bar.Close, bar.Volume, bar.TickCount);
    }
}
