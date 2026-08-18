using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.RiskCoordinator;

/// <summary>
/// The Risk Coordinator from docs/patterns/reference-real-time-risk.md §4 - map-reduce over the
/// stateless worker pool.
/// </summary>
/// <remarks>
/// <para>
/// <b>The interesting line in this whole service is the route below.</b> In production
/// <c>risk:shard</c> resolves to a Lambda-to-Lambda invoke; here it resolves to the worker pool's
/// BenzeneMessage HTTP endpoint. Everything above it - the handler, <c>ScatterGatherAsync</c>, the
/// bounded fan-out, the fold, the partial-failure policy - is byte-identical either way, because the
/// scatter goes through the routing table rather than through a transport API. That is the local
/// substitute for Lambda-to-Lambda this pattern needed, and it is a configuration difference rather
/// than a reimplementation.
/// </para>
/// <para>
/// One URL, N workers: Docker Compose's DNS round-robins <c>risk-worker</c> across the service's
/// replicas, so <c>--scale risk-worker=N</c> is the local form of "a burst of hundreds of stateless
/// workers". Nothing in the code knows how many there are.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var workerUrl = configuration["RISK_WORKER_URL"] ?? "http://risk-worker:8080/benzene-message";

        // One HttpClient for the process. The outbound middleware is transient per send, so it must
        // not own the socket handler - that is the classic socket-exhaustion shape.
        services.AddSingleton(_ => new HttpClient());

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(RiskRunHandler).Assembly)
            .AddOutboundRouting(routing => routing
                .Route(Topics.RiskShard, pipeline => pipeline
                    .UseBenzeneMessageOverHttp(workerUrl))));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());
}
