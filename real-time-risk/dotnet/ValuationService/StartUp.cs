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
        // bar is one reaction. CommitOnlyOnSuccess + CatchHandlerExceptions=false is Benzene's
        // at-least-once combination (the two are validated together at worker start-up) - if the
        // read-model call fails, the offset is not stored and the bar is redelivered, rather than
        // silently skipped and the mark lost until the next window.
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
                Topics = new[] { Topics.BarClosed },
                CatchHandlerExceptions = false,
                CommitOnlyOnSuccess = true
            },
            kafka => kafka.UseMessageHandlers()));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());
}
