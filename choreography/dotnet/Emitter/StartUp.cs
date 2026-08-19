using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Clients.CorrelationId;
using Benzene.Clients.TraceContext;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Diagnostics.Correlation;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq.RabbitMqSendMessage;
using Benzene.SelfHost;
using Benzene.Patterns.Choreography.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Benzene.Patterns.Choreography.Emitter;

/// <summary>
/// The emitter's routing table. Read it and count the entries.
/// </summary>
/// <remarks>
/// <para>
/// <b>One route. Three reactions. Four, once you start the analytics service.</b> The emitter has no
/// list of consumers to keep in step, because a fanout exchange is not a list of destinations — it is
/// a place to put an event. Compare the two-tier orchestrator's six routes to three named services:
/// there, adding a step means editing the orchestrator. Here, adding a reaction means starting one.
/// </para>
/// <para>
/// The two middleware in front of the transport are not decoration. <c>UseCorrelationId</c> and
/// <c>UseW3CTraceContext</c> stamp the outbound headers, the adapter forwards them onto the AMQP
/// message, and the consumer's headers getter lifts them back out. That is the chain that makes a
/// reaction's span a child of the emitter's — and the mesh derives consumer edges from exactly that
/// parentage. Choreography's classic complaint is that the flow is written down nowhere; the fix is
/// three lines of pipeline and keeping trace propagation on.
/// </para>
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

        // Blocking once at start-up, on purpose: the channel is a singleton shared by every send, and
        // a service that cannot reach its broker has nothing useful to do. The retry loop inside is
        // what makes this safe under compose.
        var channel = Broker.ConnectAsync(host, port, user, password).GetAwaiter().GetResult();
        services.AddSingleton(channel);

        services.AddSingleton<TenantStore>();

        services.UsingBenzene(x => x
            // The id UseCorrelationId() stamps onto every event's headers. Registered explicitly
            // because Benzene does not assume it: correlation is opt-in, and the outbound middleware
            // below is the thing that makes it leave the process.
            .AddCorrelationId()
            .AddMessageHandlers(typeof(CreateTenantHandler).Assembly)
            .AddOutboundRouting(routing => routing
                .Route(Topics.TenantCreated, pipeline => pipeline
                    .UseCorrelationId(WireHeaders.CorrelationId)
                    .UseW3CTraceContext()
                    .UseRabbitMq(channel, Broker.Exchange))));
    }

    /// <summary>
    /// HTTP is a transport, so it is declared here with every other transport — not in
    /// <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <c>UseAspNet</c> runs Kestrel as a Benzene worker, the same way <c>UseSqs</c> or
    /// <c>UseRabbitMq</c> run their consumers, so <c>Program.cs</c> is one line and contains no
    /// ASP.NET at all. The embedded alternative — <c>WebApplicationBuilder.UseBenzene&lt;StartUp&gt;()</c>
    /// plus <c>app.UseBenzene()</c> — is for putting Benzene inside a LARGER ASP.NET application that
    /// has its own controllers or minimal APIs. This service has none.
    /// <c>UseAspNet</c> binds <c>http://0.0.0.0:8080</c> by default; the <c>options</c> argument
    /// overrides it. This service reads <c>PORT</c> — the variable Cloud Run and Heroku inject, and
    /// the one you need to run two of these on one machine without Docker.
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(
                http => http.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}"));
}
