using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Clients;
using Benzene.Patterns.TransactionalOutbox.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TransactionalOutbox.Relay;

/// <summary>
/// Shape 1 of the pattern: turn the domain table's change stream into published events.
/// </summary>
/// <remarks>
/// <para>
/// In production this is a Lambda with a DynamoDB Streams event source mapping and a
/// <c>[Message("orders:INSERT")]</c> handler - a handful of lines, because Benzene's CDC transport
/// unmarshals the committed <c>NewImage</c> into a plain object for you. This local slice polls the
/// same stream with the plain AWS SDK instead, for the reason the real-time-risk example records:
/// emulating a Lambda event-source mapping locally costs a lot of moving parts for little fidelity.
/// The wire shape is identical, so swapping in the real Lambda host later changes nothing
/// downstream.
/// </para>
/// <para>
/// <b>Failure handling is the point of the relay, not an afterthought.</b> If the publish fails, the
/// shard iterator is NOT advanced past that record - the same batch is re-read and re-published
/// until it succeeds, which is what "the event cannot be lost" actually means. That mirrors the real
/// transport's behaviour: process sequentially, stop at the first failure, report that sequence
/// number as a partial-batch failure so Lambda checkpoints there and redelivers from it. CDC is
/// ordered on purpose, unlike the SQS adapter's concurrent fan-out, because change order matters.
/// </para>
/// </remarks>
public class OrderStreamRelay : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TableWaitInterval = TimeSpan.FromSeconds(2);

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonDynamoDBStreams _streams;
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<OrderStreamRelay> _logger;
    private readonly string _tableName;

    public OrderStreamRelay(
        IAmazonDynamoDB dynamoDb, IAmazonDynamoDBStreams streams, IBenzeneMessageSender sender,
        IConfiguration configuration, ILogger<OrderStreamRelay> logger)
    {
        _dynamoDb = dynamoDb;
        _streams = streams;
        _sender = sender;
        _logger = logger;
        _tableName = configuration["ORDERS_TABLE_NAME"] ?? "orders";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var streamArn = await WaitForStreamArnAsync(stoppingToken);
        _logger.LogInformation("Relaying from stream {StreamArn}", streamArn);

        var shardIterators = new Dictionary<string, string?>();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var description = await _streams.DescribeStreamAsync(
                    new DescribeStreamRequest { StreamArn = streamArn }, stoppingToken);

                foreach (var shard in description.StreamDescription.Shards.Where(s => !shardIterators.ContainsKey(s.ShardId)))
                {
                    // TRIM_HORIZON: start at the OLDEST record in the shard, not at the newest.
                    // That is what lets the relay be started after the writes and still emit every
                    // event - the demo the smoke test runs, and the reason a relay outage is a delay
                    // rather than a loss.
                    var iterator = await _streams.GetShardIteratorAsync(new GetShardIteratorRequest
                    {
                        StreamArn = streamArn,
                        ShardId = shard.ShardId,
                        ShardIteratorType = ShardIteratorType.TRIM_HORIZON
                    }, stoppingToken);
                    shardIterators[shard.ShardId] = iterator.ShardIterator;
                }

                foreach (var shardId in shardIterators.Keys.ToList())
                {
                    var iterator = shardIterators[shardId];
                    if (iterator is null)
                    {
                        continue; // closed and fully drained
                    }

                    var records = await _streams.GetRecordsAsync(new GetRecordsRequest { ShardIterator = iterator }, stoppingToken);

                    var published = 0;
                    foreach (var record in records.Records)
                    {
                        if (!await TryPublishAsync(record, stoppingToken))
                        {
                            // Do NOT advance the iterator. The next poll re-reads from here, so the
                            // event is retried until it lands. Advancing past a failed publish is
                            // exactly how an outbox silently loses the thing it exists to protect.
                            _logger.LogWarning("Publish failed; holding the iterator and retrying from this record");
                            published = -1;
                            break;
                        }

                        published++;
                    }

                    if (published >= 0)
                    {
                        shardIterators[shardId] = records.NextShardIterator;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Relay poll failed - will retry");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task<bool> TryPublishAsync(Record record, CancellationToken cancellationToken)
    {
        if (record.EventName != OperationType.INSERT)
        {
            return true;
        }

        var image = record.Dynamodb?.NewImage;
        if (image is null
            || !image.TryGetValue("orderId", out var id)
            || !image.TryGetValue("customer", out var customer)
            || !image.TryGetValue("total", out var total)
            || !decimal.TryParse(total.N, NumberStyles.Any, CultureInfo.InvariantCulture, out var amount))
        {
            _logger.LogWarning("Skipping a change record this relay cannot read");
            return true;
        }

        var evt = new OrderCreated
        {
            OrderId = id.S,
            Customer = customer.S,
            Total = amount,
            // The stream's own sequence number is the event's identity, so a redelivery after a
            // failed publish carries the SAME id as the original and the consumer's dedupe works.
            // An id minted here per attempt would defeat the whole idempotency story.
            EventId = record.Dynamodb!.SequenceNumber
        };

        try
        {
            var result = await _sender.SendAsync<OrderCreated, Contracts.PlaceOrderResponse>(Topics.OrderCreated, evt);
            if (!result.IsSuccessful)
            {
                _logger.LogWarning("Consumer rejected {OrderId} with {Status}", evt.OrderId, result.Status);
                return false;
            }

            _logger.LogInformation("Published order:created for {OrderId} (seq {Seq})", evt.OrderId, evt.EventId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Publish of {OrderId} threw", evt.OrderId);
            return false;
        }
    }

    private async Task<string> WaitForStreamArnAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var table = await _dynamoDb.DescribeTableAsync(_tableName, stoppingToken);
                if (!string.IsNullOrEmpty(table.Table.LatestStreamArn))
                {
                    return table.Table.LatestStreamArn;
                }
            }
            catch (ResourceNotFoundException)
            {
                // The Orders service provisions the table; it may still be starting.
            }

            await Task.Delay(TableWaitInterval, stoppingToken);
        }

        throw new OperationCanceledException(stoppingToken);
    }
}
