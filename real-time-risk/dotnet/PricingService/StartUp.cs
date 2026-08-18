using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Grpc;
using Benzene.Grpc.AspNet;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.RealTimeRisk.PricingService.Handlers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.PricingService;

/// <summary>
/// The Pricing Service from docs/patterns/reference-real-time-risk.md §6 - a low-latency, streaming
/// price/greeks feed over gRPC for other desks, rather than over the event bus.
/// </summary>
/// <remarks>
/// <para>
/// The only service in this platform that is not HTTP, and the point of it is how little that
/// changes: the handlers are ordinary <c>IMessageHandler</c>s with a <c>[Message]</c> topic and an
/// <c>IBenzeneResult</c>, exactly like the Trade Ledger's, and the transport is a
/// <c>UseGrpc</c> instead of a <c>UseHttp</c>. The reference doc's claim - "a Benzene service that
/// merely speaks a faster wire to its neighbours" - is meant to be legible from this file.
/// </para>
/// <para>
/// Unlike the other two services here it holds no state and talks to nothing: no DynamoDB, no table
/// provisioning, no stream to poll. That is what makes it the one service in the platform with no
/// cloud dependency at all, and why the roadmap could reach it while the market-data transport
/// decision is still open.
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
        // Reflection is on so `grpcurl` can drive this service with no local .proto file - the
        // difference between a demo somebody can poke at in one command and one that needs a
        // checkout first. Health checks are on because this platform's other two services have none,
        // which the smoke test currently works around by treating any HTTP response as "up".
        services.AddBenzeneGrpc(options =>
        {
            options.EnableReflection = true;
            options.EnableHealthChecks = true;
        });

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(GetPriceHandler).Assembly)
            .AddGrpcMessageHandlers());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseGrpc(grpc => grpc
            .UseMessageHandlers());
}
