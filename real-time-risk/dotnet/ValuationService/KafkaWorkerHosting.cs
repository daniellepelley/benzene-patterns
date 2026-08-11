using Benzene.HostedService;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// Runs a Benzene self-hosted worker (here, the Kafka consumer) alongside an ASP.NET Core HTTP
/// pipeline in one process, sharing one service container.
/// </summary>
/// <remarks>
/// <para><strong>Why this exists.</strong> Benzene's worker bindings are added through
/// <c>IBenzeneWorkerStartup</c>, reached from <c>StartUp.Configure</c> via
/// <c>app.UseWorker(...)</c>. That extension is a no-op unless the application builder <em>is</em> a
/// <c>WorkerApplicationBuilder</c> - which it is under <c>Benzene.HostedService</c>'s generic-host
/// <c>UseBenzene&lt;TStartUp&gt;()</c>, and is not under <c>Benzene.AspNet.Core</c>'s. A service that
/// wants both (a Kafka consumer <em>and</em> an HTTP query surface) therefore has to build the worker
/// itself. The Market-Data Aggregator, which is worker-only, just uses the generic host and doesn't
/// need any of this.</para>
/// <para>This is the same four lines <c>Benzene.HostedService</c>'s own <c>UseBenzene</c> runs,
/// against the web host's <see cref="IServiceCollection"/> instead of its own: the worker's pipelines
/// register into the collection ASP.NET Core will build its root provider from (so the handler, the
/// valuation store and the HTTP pipeline all resolve the same singletons, not a second copy - the
/// split-provider trap <c>AspApplicationBuilder</c> documents), and the worker instance itself is
/// created from that real root provider once the host is built.</para>
/// <para>A framework-side <c>UseWorker</c> that also worked on the ASP.NET builder would remove this
/// file; it is worth raising upstream alongside the missing outbound-Kafka route (see
/// <c>MarketDataAggregator/OutboundKafkaRouting.cs</c>).</para>
/// </remarks>
internal static class KafkaWorkerHosting
{
    public static IServiceCollection AddBenzeneWorker(this IServiceCollection services, Action<IBenzeneWorkerStartup> configure)
    {
        var workers = new BenzeneWorkerBuilder(new MicrosoftBenzeneServiceContainer(services));
        configure(workers);

        services.AddSingleton<IHostedService>(provider =>
            new BenzeneHostedServiceAdapter(workers.Create(new MicrosoftServiceResolverFactory(provider))));

        return services;
    }
}
