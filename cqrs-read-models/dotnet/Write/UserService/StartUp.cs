using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Clients.CorrelationId;
using Benzene.Clients.TraceContext;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics.Correlation;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Benzene.Patterns.Cqrs.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Benzene.Patterns.Cqrs.Write.UserService;

/// <summary>
/// A core write service. One route, one event, addressed to nobody.
/// </summary>
/// <remarks>
/// There is no route to the Tenant service and none to the read model. A user carries a tenant id
/// and this service never calls anything to check it — validating across aggregates is process, and
/// process does not live in a core service.
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var host = configuration["RABBIT_HOST"] ?? "rabbitmq";
        var port = int.TryParse(configuration["RABBIT_PORT"], out var p) ? p : 5672;
        var user = configuration["RABBIT_USER"] ?? "guest";
        var password = configuration["RABBIT_PASSWORD"] ?? "guest";

        services.AddSingleton(_ => Broker.ConnectAsync(host, port, user, password).GetAwaiter().GetResult());
        services.AddSingleton<UserStore>();
        services.AddSingleton<EventLog>();

        services.UsingBenzene(x => x
            .AddCorrelationId()
            .AddMessageHandlers(typeof(CreateUserHandler).Assembly)
            .AddOutboundRouting(routing => routing
                .Route(Topics.UserCreated, pipeline => pipeline
                    .UseCorrelationId(WireHeaders.CorrelationId)
                    .UseW3CTraceContext()
                    .UseRabbitMqExchange(Broker.Exchange))));
    }

    /// <summary>
    /// HTTP is a transport, so it is declared here with every other transport — not in
    /// <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <c>UseAspNet</c> runs Kestrel as a Benzene worker, the same way <c>UseRabbitMq</c> runs its
    /// consumer — so <c>Program.cs</c> is the plain generic host and contains no ASP.NET at all. The
    /// embedded alternative (<c>WebApplicationBuilder.UseBenzene&lt;StartUp&gt;()</c> plus
    /// <c>app.UseHttp(...)</c>) is for putting Benzene inside a LARGER ASP.NET application that has
    /// its own controllers or minimal APIs; this service has none.
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(
                http => http.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}"));
}
