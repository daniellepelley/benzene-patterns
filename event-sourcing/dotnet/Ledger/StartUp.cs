using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.EventSourcing;
using Benzene.Microsoft.Dependencies;
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

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http.UseMessageHandlers());
}
