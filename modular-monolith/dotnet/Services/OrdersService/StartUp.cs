using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Benzene.Patterns.ModularMonolith.Contracts;
using Benzene.Patterns.ModularMonolith.Modules.Orders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.ModularMonolith.Services.OrdersService;

/// <summary>
/// Phase n: the Orders module in <b>its own process</b>, with Billing and Shipping a network hop
/// away.
/// </summary>
/// <remarks>
/// <para>
/// <b>Diff this against <c>Monolith/StartUp.cs</c>.</b> Three routes changed transport. That is the
/// extraction. No call site moved, no handler changed, no contract changed, no test of the Orders
/// module changed — <c>PlaceOrderHandler</c> said <c>SendAsync("billing:charge", …)</c> before and
/// says exactly that now.
/// </para>
/// <para>
/// What DID change is real and the pattern says so plainly: those three sends now cost milliseconds
/// instead of microseconds, they can now genuinely return <c>service-unavailable</c>, and the two
/// modules can no longer share a transaction even in principle. The handler already branched on
/// those statuses and already compensated instead of rolling back, because it was written that way
/// on day one — which is the part of distribution that is normally a rewrite, and here was not.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var billingUrl = configuration["BILLING_URL"] ?? "http://billing:8080/benzene-message";
        var shippingUrl = configuration["SHIPPING_URL"] ?? "http://shipping:8080/benzene-message";

        services.AddSingleton(_ => new HttpClient());
        services.AddSingleton(new DeploymentName("extracted"));

        services.UsingBenzene(x => x
            // Only the Orders module's handlers live here now. Billing's and Shipping's assemblies
            // are not even referenced by this project - the seam was a message contract, so removing
            // the code is a project-reference deletion rather than an untangling.
            .AddMessageHandlers(typeof(PlaceOrderHandler).Assembly)

            // ── THE ROUTING TABLE ────────────────────────────────────────────────────────────
            // The same three topics. The same call sites. A different transport.
            //
            //   Monolith:  .Route(Topics.BillingCharge, p => p.UseInProcess())
            //   Extracted: .Route(Topics.BillingCharge, p => p.UseBenzeneMessageOverHttp(billingUrl))
            .AddOutboundRouting(routing => routing
                .Route(Topics.BillingCharge, p => p.UseBenzeneMessageOverHttp(billingUrl))
                .Route(Topics.BillingRefund, p => p.UseBenzeneMessageOverHttp(billingUrl))
                .Route(Topics.ShippingReserve, p => p.UseBenzeneMessageOverHttp(shippingUrl))));
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
