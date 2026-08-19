using Amazon.DynamoDBv2;
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Clients;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Benzene.Patterns.TransactionalOutbox.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.TransactionalOutbox.OrdersService;

/// <summary>
/// The order-owning service. Writes rows; publishes nothing.
/// </summary>
/// <remarks>
/// The routing table below carries exactly one route, and it is only there to serve the <b>naive</b>
/// endpoint. The CDC path has no outbound route at all, because it makes no outbound call - which is
/// what "the event is a consequence of the committed write" means in code.
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var notificationsUrl = configuration["NOTIFICATIONS_URL"] ?? "http://notifications:8080/benzene-message";

        services.AddSingleton(_ => DynamoDbClientFactory.Create(configuration));
        services.AddSingleton(_ => new HttpClient());

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(PlaceOrderHandler).Assembly)
            .AddOutboundRouting(routing => routing
                .Route(Topics.OrderCreated, p => p.UseBenzeneMessageOverHttp(notificationsUrl))));
    }

    /// <summary>
    /// HTTP is a transport, so it is declared here with every other transport — not in
    /// <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <c>UseAspNet</c> runs Kestrel as a Benzene worker, the same way <c>UseSqs</c> or
    /// <c>UseRabbitMq</c> run their consumers, so the entry point contains no ASP.NET at all — only
    /// the table provisioning this service does before it starts serving.
    /// <c>UseAspNet</c>'s optional second argument is the port knob: it binds
    /// <c>http://0.0.0.0:8080</c> by default, and <c>options =&gt; options.Urls = …</c> overrides that.
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(http => http.UseMessageHandlers()));
}
