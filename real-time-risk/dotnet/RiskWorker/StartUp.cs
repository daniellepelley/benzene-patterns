using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.RealTimeRisk.PricingService;
using Grpc.Net.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.RiskWorker;

/// <summary>
/// The stateless risk worker from docs/patterns/reference-real-time-risk.md §4 - the thing the Risk
/// Coordinator scatters <c>risk:shard</c> across.
/// </summary>
/// <remarks>
/// <para>
/// It serves the <b>BenzeneMessage endpoint</b> (<c>POST /benzene-message</c>) rather than a REST
/// route, because that is the HTTP counterpart of the Lambda invoke path this would use in
/// production: the topic travels inside the envelope, so one endpoint serves every topic and the
/// receiving side routes on it exactly as a Lambda-hosted worker would. Swapping the two is a
/// hosting change, not a rewrite.
/// </para>
/// <para>
/// The endpoint is documented as opt-in and not for unauthenticated production exposure. That is
/// exactly right, and it is why it is used HERE: this is a private worker on a Compose network, not
/// an edge service.
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
        var readModelsUrl = configuration["READ_MODELS_URL"] ?? "http://risk-read-models:8080";
        var pricingUrl = configuration["PRICING_URL"] ?? "http://pricing:8080";

        services.AddSingleton(_ => new PositionSource(new HttpClient(), readModelsUrl));
        services.AddSingleton(_ => new MarkToMarket(
            // The Pricing Service speaks h2c on the Compose network - no TLS, no certificates to
            // distribute - so the channel is told to use unencrypted HTTP/2 explicitly.
            new Pricing.PricingClient(GrpcChannel.ForAddress(pricingUrl, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true }
            }))));

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(RiskShardHandler).Assembly));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseBenzeneMessage(message => message
                .UseMessageHandlers()));
}
