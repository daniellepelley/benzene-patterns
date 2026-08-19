using Benzene.Abstractions.DI;
using Benzene.Abstractions.Hosting;
using Benzene.Abstractions.Messages.Mappers;
using Benzene.Abstractions;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.MessageHandlers;
using Benzene.Core.Middleware;
using Benzene.Diagnostics.Correlation;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Diagnostics;
using Benzene.Idempotency;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq.RabbitMqMessage;
using Benzene.RabbitMq;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Benzene.Patterns.Choreography.Reactions.CacheWarmer;

/// <summary>
/// A reaction: one queue in, one handler, nothing out.
/// </summary>
/// <remarks>
/// <para>
/// There is no outbound routing here at all. This service cannot call anybody, and nobody calls it —
/// it reacts to an event and that is the whole of its interface. Compare the emitter, which has a
/// routing table with one entry and no idea this file exists.
/// </para>
/// <para>
/// <c>UseIdempotency()</c> is not optional decoration. Every broker in this family — SNS, SQS,
/// EventBridge, Service Bus, RabbitMQ — is at-least-once, so a reaction that is not idempotent is a
/// reaction that will eventually do its thing twice. The middleware derives a key from the topic and
/// body, claims it atomically, and short-circuits a duplicate before the handler runs. Note what it
/// does NOT do: a handler that FAILS releases the claim, so a genuine failure is still retried
/// rather than permanently suppressed by its own first attempt.
/// </para>
/// <para>
/// The in-memory store is right for one process. A fleet of instances needs a shared
/// <c>IIdempotencyStore</c> over an atomic conditional write — DynamoDB <c>attribute_not_exists</c>,
/// Redis <c>SET NX</c> — and the seam for that is the same interface.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<Journal>();
        services.UsingBenzene(x => x
            .AddCorrelationId()
            .AddMessageHandlers(typeof(WarmCacheOnTenantCreated).Assembly)
            .AddInMemoryIdempotencyStore());
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
    {
        var host = configuration["RABBIT_HOST"] ?? "rabbitmq";
        var port = int.TryParse(configuration["RABBIT_PORT"], out var p) ? p : 5672;
        var user = configuration["RABBIT_USER"] ?? "guest";
        var password = configuration["RABBIT_PASSWORD"] ?? "guest";

        var config = new RabbitMqConfig
        {
            QueueName = Topology.Queue,
            // One at a time, so the journal reads in the order the events arrived. A real reaction
            // would raise both and accept that RabbitMQ makes no ordering promise once more than one
            // delivery is in flight.
            ConcurrentRequests = 1,
            PrefetchCount = 1
        };

        var connectionFactory = new RabbitMqConnectionFactory(new ConnectionFactory
        {
            HostName = host,
            Port = port,
            UserName = user,
            Password = password
        });

        app.UseWorker(workers => workers.UseRabbitMq(config, connectionFactory,
            rabbit => rabbit
                // First, so every span below nests under the emitter's. This is the middleware that
                // makes the choreography graph derivable: the mesh reads consumer edges off trace
                // parentage, so a reaction whose span has no remote parent is a reaction the fleet
                // view cannot connect to the event that caused it.
                .UseW3CTraceContext()
                .Use("RestoreCorrelationId", async (IServiceResolver resolver, RabbitMqContext context, Func<Task> next) =>
                {
                    // Six lines that Benzene does not ship a counterpart for, written out FOUR TIMES
                    // in this example - once per reaction. Benzene.Clients stamps the correlation id
                    // on the way OUT (Benzene.Clients.CorrelationId.UseCorrelationId, on
                    // OutboundContext), and the inbound side reads it onto the diagnostics span - but
                    // Benzene.Diagnostics.Correlation ships only AddCorrelationId(), which registers a
                    // scoped ICorrelationId holding a fresh Guid. Nothing puts the RECEIVED id back
                    // into it, so without this a reaction's own correlation id is that fresh Guid and
                    // the chain breaks exactly where a reader would look for it. Half a convention:
                    // the producer side has its steer, the consumer side has none.
                    var headers = resolver.GetService<IMessageHeadersGetter<RabbitMqContext>>()
                        .GetHeaders(context);
                    if (headers.TryGetValue(WireHeaders.CorrelationId, out var correlationId))
                    {
                        resolver.GetService<ICorrelationId>().Set(correlationId);
                    }
                    await next();
                })
                .UseIdempotency()
                .UseMessageHandlers()));
    }
}
