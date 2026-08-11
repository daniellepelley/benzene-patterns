using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Kafka.Core;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// The Valuation Service from docs/patterns/reference-real-time-risk.md §2: subscribes to
/// <see cref="Topics.BarClosed"/> and re-marks every position exposed to that symbol
/// (docs/patterns/choreography.md - it reacts, it does not get told).
/// </summary>
/// <remarks>
/// Two entry points in one process: a Kafka consumer for the event, and an HTTP endpoint for the
/// resulting query view. The same <c>[Message]</c>-annotated handlers serve both - the Kafka pipeline
/// routes <c>bar-closed</c> to <see cref="RevaluePositionsOnBarClosed"/> and the HTTP pipeline routes
/// <c>GET /valuations/by-symbol/{symbol}</c> to <see cref="SymbolValuationsQueryHandler"/>, off one
/// handler registry. See <see cref="KafkaWorkerHosting"/> for why the worker half is wired by hand.
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.UsingBenzene(x => x
            .AddSingleton<PositionValuationStore>()
            .AddMessageHandlers(typeof(SymbolValuationsQueryHandler).Assembly));

        services.AddHttpClient<RiskReadModelsClient>(client =>
        {
            client.BaseAddress = new Uri(
                (configuration["RISK_READ_MODELS_URL"] ?? "http://localhost:8082").TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // The inbound half of the choreography. Per-record UseKafka, not UseKafkaStream: one closed
        // bar is one reaction.
        //
        // Offsets are left on Confluent.Kafka's defaults, and that is a decision, not an oversight.
        // BenzeneKafkaConfig.CommitOnlyOnSuccess only withholds an offset when the *pipeline* throws,
        // and a handler exception never gets that far: Benzene's MessageHandler catches it and
        // returns ServiceUnavailable (see Benzene.Core.MessageHandlers/MessageHandler.cs), so the
        // record is acknowledged either way. Turning it on would therefore buy nothing here while
        // dragging in its mandatory CatchHandlerExceptions=false, which stops the whole worker on the
        // first pipeline-level fault - a bad trade for a service whose dependency is another
        // service's HTTP endpoint.
        //
        // Verified by running it: with Risk Read Models down, each bar's revaluation failed, was
        // logged, and was skipped; when the read model came back the *next* bar produced a complete,
        // correct valuation for every exposed book. That is safe precisely because this reaction is a
        // full recomputation from current read-model state, not an increment - a skipped bar costs
        // one bar interval of staleness and nothing accumulates wrong. (The aggregator's fold *does*
        // accumulate, which is exactly why that side has real checkpointing and a replay guard.)
        // A service that needed the bar itself to survive would have to fail the pipeline, not the
        // handler.
        services.AddBenzeneWorker(worker => worker.UseKafka<string, string>(
            new BenzeneKafkaConfig
            {
                ConsumerConfig = new ConsumerConfig
                {
                    BootstrapServers = configuration["KAFKA_BOOTSTRAP_SERVERS"] ?? "localhost:9092",
                    GroupId = configuration["KAFKA_GROUP_ID"] ?? "valuation-service",
                    AutoOffsetReset = AutoOffsetReset.Earliest,
                    PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky
                },
                // The Benzene topic and the Kafka topic are the same string on this transport - see
                // Topics.BarClosed's remarks - so subscribing to the Kafka topic is what routes
                // [Message("bar-closed")].
                Topics = new[] { Topics.BarClosed }
            },
            kafka => kafka.UseMessageHandlers()));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());
}
