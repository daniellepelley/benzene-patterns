using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.TwoTier.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.TwoTier.Orchestrator;

/// <summary>
/// The orchestrator's routing table: one entry per core-service operation it drives.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the two tiers' coupling — six topics, resolved to three destinations. The
/// saga in <c>SignupHandler</c> names only topics; this file is where they become addresses, which
/// is what lets the same orchestrator run against Lambda-to-Lambda invokes in production and HTTP
/// containers here.
/// </para>
/// <para>
/// Note the arrows point one way. The orchestrator routes to core services; no core service has an
/// outbound route at all. That is the tiering, enforced by what each host registers.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var tenant = configuration["TENANT_URL"] ?? "http://tenant:8080/benzene-message";
        var user = configuration["USER_URL"] ?? "http://user:8080/benzene-message";
        var billing = configuration["BILLING_URL"] ?? "http://billing:8080/benzene-message";

        services.AddSingleton(_ => new HttpClient());

        services.UsingBenzene(x => x
            .AddMessageHandlers(typeof(SignupHandler).Assembly)
            .AddOutboundRouting(routing => routing
                .Route(Topics.TenantCreate, p => p.UseBenzeneMessageOverHttp(tenant))
                .Route(Topics.TenantDelete, p => p.UseBenzeneMessageOverHttp(tenant))
                .Route(Topics.UserCreate, p => p.UseBenzeneMessageOverHttp(user))
                .Route(Topics.UserDelete, p => p.UseBenzeneMessageOverHttp(user))
                .Route(Topics.BillingSetup, p => p.UseBenzeneMessageOverHttp(billing))
                .Route(Topics.BillingTeardown, p => p.UseBenzeneMessageOverHttp(billing))));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http.UseMessageHandlers());
}
