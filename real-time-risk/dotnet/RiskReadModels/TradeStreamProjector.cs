using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Consumes the Trade Ledger's DynamoDB Stream and folds each <see cref="TradeBooked"/> event into
/// <see cref="BookPositionsStore"/>. In production this is exactly the job AWS Lambda's DynamoDB
/// Streams event source mapping does (see <c>Benzene.Aws.Lambda.DynamoDb</c>, whose
/// <c>[Message("trades:INSERT")]</c> handler shape this projector's <see cref="Apply"/> mirrors by
/// hand); this local/Docker Compose slice polls directly instead of running inside a real Lambda,
/// because LocalStack's Lambda + Streams event-source-mapping emulation adds a lot of moving parts
/// for comparatively little fidelity gain over polling the same stream with the plain AWS SDK - see
/// real-time-risk/README.md. A future slice could swap this for the real Lambda-hosted handler
/// without changing anything downstream: the wire shape (topic + JSON body) is identical.
/// </summary>
/// <remarks>
/// Simplifications deliberately made for this demo, not production-grade streaming code: shard
/// iterators are held only in memory (no checkpoint store, so a restart re-reads from
/// <see cref="ShardIteratorType.TRIM_HORIZON"/> - safe here because <see cref="BookPositionsStore.Apply"/>
/// is idempotent per (book, version)), and shard splits/merges are handled by simply re-describing the
/// stream each loop rather than a full shard-lineage walk (fine for a single, low-volume demo table
/// that is very unlikely to split a shard).
/// </remarks>
public class TradeStreamProjector : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TableWaitInterval = TimeSpan.FromSeconds(2);

    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IAmazonDynamoDBStreams _streams;
    private readonly BookPositionsStore _store;
    private readonly ILogger<TradeStreamProjector> _logger;
    private readonly string _tableName;

    public TradeStreamProjector(
        IAmazonDynamoDB dynamoDb,
        IAmazonDynamoDBStreams streams,
        BookPositionsStore store,
        IConfiguration configuration,
        ILogger<TradeStreamProjector> logger)
    {
        _dynamoDb = dynamoDb;
        _streams = streams;
        _store = store;
        _logger = logger;
        _tableName = configuration["TRADES_TABLE_NAME"] ?? "trades";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var streamArn = await WaitForStreamArnAsync(stoppingToken);
        _logger.LogInformation("Projecting from stream {StreamArn}", streamArn);

        var shardIterators = new Dictionary<string, string?>();

        while (!stoppingToken.IsCancellationRequested)
        {
            var description = await _streams.DescribeStreamAsync(
                new DescribeStreamRequest { StreamArn = streamArn }, stoppingToken);

            foreach (var shard in description.StreamDescription.Shards)
            {
                if (shardIterators.ContainsKey(shard.ShardId))
                {
                    continue;
                }

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
                    // A null NextShardIterator means the shard is closed and fully drained - nothing left to read.
                    continue;
                }

                var records = await _streams.GetRecordsAsync(new GetRecordsRequest { ShardIterator = iterator }, stoppingToken);
                foreach (var record in records.Records)
                {
                    Apply(record);
                }
                shardIterators[shardId] = records.NextShardIterator;
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private void Apply(Record record)
    {
        if (record.EventName != OperationType.INSERT)
        {
            return;
        }

        var image = record.Dynamodb?.NewImage;
        if (image is null
            || !image.TryGetValue("eventType", out var eventTypeAttr) || eventTypeAttr.S != Topics.TradeBookedEventType
            || !image.TryGetValue("payload", out var payloadAttr) || string.IsNullOrEmpty(payloadAttr.S)
            || !image.TryGetValue("version", out var versionAttr) || !long.TryParse(versionAttr.N, out var version))
        {
            return;
        }

        var trade = System.Text.Json.JsonSerializer.Deserialize<TradeBooked>(payloadAttr.S);
        if (trade is null)
        {
            _logger.LogWarning("Skipping unparseable TradeBooked payload at version {Version}", version);
            return;
        }

        _store.Apply(trade, version);
        _logger.LogInformation("Projected trade {TradeId} into book {Book} at version {Version}", trade.TradeId, trade.Book, version);
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
                // The compose bootstrap step that creates the table may still be running.
            }

            _logger.LogInformation("Waiting for the {Table} table (and its stream) to be provisioned...", _tableName);
            await Task.Delay(TableWaitInterval, stoppingToken);
        }

        throw new OperationCanceledException(stoppingToken);
    }
}
