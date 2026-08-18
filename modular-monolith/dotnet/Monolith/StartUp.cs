using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Clients.InProcess;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.ModularMonolith.Contracts;
using Benzene.Patterns.ModularMonolith.Modules.Billing;
using Benzene.Patterns.ModularMonolith.Modules.Orders;
using Benzene.Patterns.ModularMonolith.Modules.Shipping;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.ModularMonolith.Monolith;

/// <summary>
/// Phase 0: <b>one deliverable</b>. Orders, Billing and Shipping in one process, addressing each
/// other by topic through in-process routes.
/// </summary>
/// <remarks>
/// <para>
/// Compare this file with <c>Services/OrdersService/StartUp.cs</c>. The module assemblies are the
/// same, the handlers are the same, the contracts are the same; the routing table is the only thing
/// that differs, and it differs in two lines. That is the claim the whole pattern rests on, and it
/// is checked here rather than asserted: the smoke test runs the same order through both stacks and
/// requires the same answer.
/// </para>
/// <para>
/// Two properties make the in-process route a rehearsal for distribution rather than a shortcut
/// around it, and both are the transport's doing, not this file's: each dispatch runs in its own
/// fresh DI scope - the isolation a real cross-process call would have - and the payload is
/// serialized, so no shared mutable object can sneak across by reference. A module that cheats
/// against rule 3 fails here, in development, rather than at extraction.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Each module's own data, owned outright. Nothing reads another module's store (rule 2) -
        // in one process nothing would STOP it, which is exactly why the rule is a review rule.
        services.AddSingleton<BillingStore>();
        services.AddSingleton<ShippingStore>();
        services.AddSingleton(new DeploymentName("monolith"));

        services.UsingBenzene(x => x
            .AddMessageHandlers(
                typeof(PlaceOrderHandler).Assembly,
                typeof(ChargeCardHandler).Assembly,
                typeof(ReserveStockHandler).Assembly)

            // The inbound half of the in-process transport: a BenzeneMessage pipeline over the
            // modules' handlers, registered as the dispatch target.
            .AddInProcessMessaging(pipeline => pipeline
                .UseMessageHandlers())

            // ── THE ROUTING TABLE ────────────────────────────────────────────────────────────
            // The one place addresses become destinations. Extraction is an edit here and nowhere
            // else; see Services/OrdersService/StartUp.cs for the same three routes pointed at
            // three processes.
            .AddOutboundRouting(routing => routing
                .Route(Topics.BillingCharge, p => p.UseInProcess())
                .Route(Topics.BillingRefund, p => p.UseInProcess())
                .Route(Topics.ShippingReserve, p => p.UseInProcess())));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());
}
