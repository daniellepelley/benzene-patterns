using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Core.Middleware;
using Benzene.Http;
using Benzene.Idempotency;
using Benzene.Microsoft.Dependencies;
using Benzene.RabbitMq;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// Both halves of CQRS in one startup: a RabbitMQ worker that projects, and an HTTP pipeline that
/// queries.
/// </summary>
/// <remarks>
/// <para>
/// <c>UseAspNet</c> mounts Kestrel as a peer worker beside <c>UseRabbitMq</c>, so the write side of
/// the read model (the projection) and its read side (the queries) share one process, one container
/// and one store, with no second Benzene container to keep in step.
/// </para>
/// <para>
/// Splitting them into two deployables over a shared database is the usual production shape — the
/// projector scales on event volume and the query side on read volume, which is half the point of
/// CQRS. Nothing here would change but the hosting: the handlers already do not know about each
/// other.
/// </para>
/// <para>
/// Note the absence: this service has <b>no outbound routing</b>. It consumes events and answers
/// queries. A read model that calls a core service to fill a gap has stopped being a projection and
/// become the runtime fan-out it exists to replace.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var tenantEvents = configuration["TENANT_EVENTS_URL"] ?? "http://tenant:8080/events";
        var userEvents = configuration["USER_EVENTS_URL"] ?? "http://user:8080/events";

        services.AddSingleton<ReadStore>();
        services.AddSingleton(new ReplaySources(tenantEvents, userEvents));
        services.AddSingleton(_ => new HttpClient());

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(ProjectTenantCreated).Assembly)
            .AddHttpMessageHandlers()
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

        // A deliberate, configurable delay in front of the projection. Eventual consistency is real
        // and invisible on a laptop - the broker is fast enough that the read model has usually
        // caught up before you can type the next curl - so the property that MATTERS in production
        // becomes the property nobody ever sees in the demo. Setting PROJECTION_DELAY_MS makes the
        // lag window observable, and the smoke test uses it to assert what a reader is otherwise
        // asked to take on trust: right after the write, the authority answers and the view does not.
        var lagMs = int.TryParse(configuration["PROJECTION_DELAY_MS"], out var lag) ? lag : 0;

        app.UseWorker(worker => worker
            .UseAspNet(
                asp => asp.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}")
            // UseIdempotency guards against a REDELIVERY doing the work twice. It is belt and braces
            // here rather than the load-bearing thing it is elsewhere: the folds converge anyway,
            // which is what makes replay safe. A projection that needs idempotency middleware to be
            // correct is a projection that will not survive its first rebuild.
            .UseRabbitMq(config, connectionFactory, rabbit => rabbit
                .Use("ProjectionLag", async (context, next) =>
                {
                    if (lagMs > 0)
                    {
                        await Task.Delay(lagMs);
                    }

                    await next();
                })
                .UseIdempotency()
                .UseMessageHandlers()));
    }
}
