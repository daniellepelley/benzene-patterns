using System.Text.Json;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Confluent.Kafka;

// ---------------------------------------------------------------------------------------------
// A synthetic market-data feed: a demo-only fixture, deliberately kept as its own tiny project.
//
// There is no real tick feed behind this platform, and something has to put ticks on
// `market-data-ticks` for the Market-Data Aggregator to have anything to aggregate - the continuous
// equivalent of the `curl -X POST /trades` that drives the Trade Ledger.
//
// Why a separate project rather than an env-var-gated hosted service inside the aggregator: this is
// a fake, and a fake that ships inside the real service's image is one config mistake away from
// running in an environment that has a real feed. Keeping it out here means the aggregator's image
// contains no demo code at all, the "producer" is a genuinely separate process exactly as it would
// be in production, and deleting the fixture the day a real feed exists is deleting a folder and a
// compose service - not surgery on a service that ships.
//
// It also, usefully, demonstrates the real property that the aggregator consumes a NON-Benzene
// producer: a tick is a raw JSON record with no Benzene envelope, which is what a market-data feed
// actually looks like (see MarketTick's remarks). Nothing here references Benzene.
// ---------------------------------------------------------------------------------------------

var bootstrapServers = Environment.GetEnvironmentVariable("KAFKA_BOOTSTRAP_SERVERS") ?? "localhost:9092";
var topic = Environment.GetEnvironmentVariable("MARKET_DATA_TOPIC") ?? Topics.MarketDataTicksKafkaTopic;
var intervalMs = int.TryParse(Environment.GetEnvironmentVariable("TICK_INTERVAL_MS"), out var parsed) ? parsed : 250;
var symbols = (Environment.GetEnvironmentVariable("SYMBOLS") ?? "AAPL,MSFT,GOOG")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

var jsonOptions = new JsonSerializerOptions(JsonSerializerOptions.Web);
var random = new Random(20260811);
var prices = new Dictionary<string, decimal>
{
    ["AAPL"] = 150.00m,
    ["MSFT"] = 410.00m,
    ["GOOG"] = 175.00m
};

using var producer = new ProducerBuilder<string, string>(new ProducerConfig
{
    BootstrapServers = bootstrapServers,
    Acks = Acks.All,
    // Retry rather than die while the broker finishes coming up under `docker compose up`; the
    // aggregator and the broker start at the same moment and the broker is slower.
    MessageSendMaxRetries = 10
}).Build();

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    shutdown.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdown.Cancel();

Console.WriteLine($"Producing synthetic ticks for {string.Join(", ", symbols)} to {topic} on {bootstrapServers} every {intervalMs}ms");

while (!shutdown.IsCancellationRequested)
{
    foreach (var symbol in symbols)
    {
        var last = prices.TryGetValue(symbol, out var known) ? known : 100.00m;

        // A plain random walk: +/- up to 0.25% per tick, floored so a long run can't wander to zero.
        var drift = (decimal)((random.NextDouble() - 0.5) * 0.005);
        var price = Math.Round(Math.Max(1.00m, last * (1 + drift)), 2);
        prices[symbol] = price;

        var tick = new MarketTick
        {
            Symbol = symbol,
            Price = price,
            Size = random.Next(1, 20) * 10,
            Timestamp = DateTimeOffset.UtcNow
        };

        try
        {
            // The message KEY is the symbol - the single most load-bearing line in this file. Kafka's
            // default partitioner hashes the key, so every tick for a symbol lands on one partition
            // and stays ordered there, which is what gives the aggregator its shard-per-symbol
            // grouping via PartitionBy(r => r.TopicPartition) with no custom partitioner anywhere.
            await producer.ProduceAsync(topic, new Message<string, string>
            {
                Key = symbol,
                Value = JsonSerializer.Serialize(tick, jsonOptions)
            }, shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            break;
        }
        catch (ProduceException<string, string> ex)
        {
            Console.WriteLine($"Produce failed ({ex.Error.Reason}) - retrying on the next tick");
            await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        }
    }

    try
    {
        await Task.Delay(intervalMs, shutdown.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

producer.Flush(TimeSpan.FromSeconds(5));
