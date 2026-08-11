using Benzene.Abstractions.Hosting;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Kafka.Core;
using Benzene.Kafka.Streaming;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.SelfHost;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator;

/// <summary>
/// The Market-Data Aggregator from docs/patterns/reference-real-time-risk.md §1: ingest a firehose
/// of ticks partitioned by symbol, roll them into one-minute OHLC bars, and emit
/// <see cref="Topics.BarClosed"/> per closed bar.
/// </summary>
/// <remarks>
/// <para>The reference doc ingests from Kinesis; this ingests the same records, with the same
/// handler, from Kafka - "the same handlers, different host", and the reason it is a legitimate swap
/// rather than a downgrade is that Kafka now has the windowed/partitioned/checkpointed binding it
/// previously lacked: <c>Benzene.Kafka.Streaming</c>'s <c>UseKafkaStream</c>. A Kafka broker runs
/// locally with no account and no signup, which <c>UseKinesisStream</c> could not have matched (see
/// real-time-risk/README.md on LocalStack's 2026 account requirement).</para>
/// <para>Hosted as a pure worker (<c>Benzene.HostedService</c>'s generic-host <c>UseBenzene</c>) - it
/// has no HTTP surface of its own, and <c>UseWorker(...)</c> is only honored on the worker
/// application builder. The Valuation Service, which needs both, wires its worker by hand; see
/// <c>ValuationService/KafkaWorkerHosting.cs</c>.</para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Built here rather than resolved from DI inside the route: an outbound route is configured
        // once, at start-up, with no service resolver in scope - the same way UseKafkaClient(producer)
        // and UseSns(topicArn) take their transport handle directly. Confluent's producer is
        // thread-safe and meant to be shared for the process's lifetime.
        var producer = CreateProducer(configuration);

        services.UsingBenzene(x => x
            .AddSingleton(TimeProvider.System)
            .AddSingleton(_ => new OhlcBarAggregator(BarInterval(configuration)))
            .AddSingleton<StreamReplayGuard>()
            .AddSingleton(producer)
            // Where bar-closed goes is a routing-table entry declared once at start-up, exactly as
            // docs/patterns/choreography.md prescribes - the handler only ever names the topic.
            .AddOutboundRouting(routing => routing
                .Route(Topics.BarClosed, pipeline => pipeline.UseKafka(producer, keyHeader: "symbol"))));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker.UseKafkaStream<string, string>(
            KafkaConfig(configuration),
            stream => stream.Use(resolver => new OhlcAggregationMiddleware(
                resolver.GetService<OhlcBarAggregator>(),
                resolver.GetService<StreamReplayGuard>(),
                resolver.GetService<IBenzeneMessageSender>(),
                resolver.GetService<TimeProvider>(),
                resolver.GetService<ILogger<OhlcAggregationMiddleware>>())),
            StreamOptions(configuration)));

    private static BenzeneKafkaConfig KafkaConfig(IConfiguration configuration) => new()
    {
        ConsumerConfig = new ConsumerConfig
        {
            BootstrapServers = BootstrapServers(configuration),
            GroupId = configuration["KAFKA_GROUP_ID"] ?? "market-data-aggregator",
            // Earliest, not the Kafka default of Latest: on a cold `docker compose up` the tick
            // generator may well have produced before this consumer group first joined, and silently
            // skipping everything before the join makes the demo look broken for a whole bar.
            AutoOffsetReset = AutoOffsetReset.Earliest,
            // Cooperative rebalancing is what Benzene.Kafka.Streaming recommends: this worker keeps
            // rolling bar state in memory, so a stop-the-world rebalance that reshuffles partitions
            // costs more here than the usual "brief pause".
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
        },
        Topics = new[] { configuration["MARKET_DATA_TOPIC"] ?? Topics.MarketDataTicksKafkaTopic }
    };

    private static KafkaStreamOptions StreamOptions(IConfiguration configuration) => new()
    {
        MaxBatchSize = 500,
        // A quarter-second window: this is the added latency between a tick landing and the batch it
        // is in being folded into a bar, and a demo feed produces a handful of ticks a second, so the
        // 1s default would mean visibly laggy batches carrying almost nothing.
        MaxBatchWait = TimeSpan.FromMilliseconds(250),
        // Left on (the default) as a backstop only. This handler checkpoints each partition's own
        // frontier explicitly as it finishes that partition's records - see
        // OhlcAggregationMiddleware - so auto-checkpoint has nothing left to do on the happy path.
        AutoCheckpointOnSuccess = true
    };

    private static TimeSpan BarInterval(IConfiguration configuration)
    {
        // One minute is the pattern's bar width and the default here. The override exists purely so
        // the local demo and CI don't have to wait a full minute to see the first bar close; nothing
        // else in the design depends on the value. See docker-compose.yml.
        var seconds = configuration.GetValue<int?>("BAR_INTERVAL_SECONDS") ?? 60;
        return TimeSpan.FromSeconds(seconds);
    }

    private static string BootstrapServers(IConfiguration configuration)
        => configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092";

    private static IProducer<string, string> CreateProducer(IConfiguration configuration)
        => new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = BootstrapServers(configuration),
            // A closed bar is a fact other services act on, so wait for all in-sync replicas rather
            // than fire-and-forget at the broker level. (Fire-and-forget is the *Benzene* semantic -
            // nobody waits for a reply - not a licence to lose the message on the way out.)
            Acks = Acks.All,
            EnableIdempotence = true
        }).Build();
}
