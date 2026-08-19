using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.EventSourcing;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.EventSourcing.Ledger;

/// <summary>
/// One service, one store, no broker. Read the registrations and you can see the whole pattern's
/// division of labour.
/// </summary>
/// <remarks>
/// <para>
/// <c>AddInMemoryEventStore()</c> is the entire Benzene contribution: an append-only log with
/// optimistic concurrency. <c>Rehydrator</c> and <c>SnapshotStore</c> are this example's own code,
/// and that is not a shortcoming — <c>Benzene.EventSourcing</c> deliberately imposes no aggregate
/// base class, no snapshot type and no replay driver, because those conventions vary enough between
/// domains that a framework abstraction usually gets in the way.
/// </para>
/// <para>
/// Swapping in the DynamoDB store (<c>AddDynamoDbEventStore("accounts")</c>) is a one-line change and
/// nothing else here moves; the real-time-risk example's Trade Ledger does exactly that. In-memory
/// keeps this example to a single container and makes the concurrency assertions deterministic.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<SnapshotStore>();
        services.AddScoped<Rehydrator>();

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(OpenAccountHandler).Assembly)
            .AddInMemoryEventStore());
    }

    /// <summary>
    /// HTTP is a transport, so it is declared here with every other transport — not in
    /// <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>UseAspNet</c> runs Kestrel as a Benzene worker, the same way <c>UseSqs</c> or
    /// <c>UseRabbitMq</c> run their consumers. Which means <c>Program.cs</c> is the plain generic
    /// host and contains no ASP.NET at all: adding a queue consumer to this service later is another
    /// line in this method, and the program's shape does not change.
    /// </para>
    /// <para>
    /// The alternative — <c>WebApplicationBuilder.UseBenzene&lt;StartUp&gt;()</c> plus
    /// <c>app.UseBenzene()</c> in <c>Program.cs</c>, with <c>app.UseHttp(...)</c> here — is for
    /// embedding Benzene inside a <em>larger</em> ASP.NET application that has its own controllers or
    /// minimal APIs to serve. This ledger has none: every route it answers is a Benzene handler, so
    /// ASP.NET is purely the HTTP host and belongs inside the worker.
    /// </para>
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(
                http => http.UseMessageHandlers(),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}"));
}
