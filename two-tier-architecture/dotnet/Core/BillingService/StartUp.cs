using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.TwoTier.Core.BillingService;

/// <summary>
/// A core service: one aggregate, its own store, CRUD topics, no process logic.
/// </summary>
/// <remarks>
/// It serves a BenzeneMessage endpoint (so the orchestrator reaches it by topic, as it would a
/// Lambda-to-Lambda invoke in production) and a couple of plain HTTP reads so a reader can check
/// what a rolled-back saga left behind. It has no outbound routing at all - a core service calls
/// nobody, which is what keeps the dependency arrows pointing one way.
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<BillingStore>();
        services.UsingBenzene(x => x.AddMessageHandlers(typeof(BillingStore).Assembly));
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
    /// <c>UseAspNet</c>'s optional second argument is the port knob: it binds
    /// <c>http://0.0.0.0:8080</c> by default, and <c>options =&gt; options.Urls = …</c> overrides that.
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(http => http
                .UseBenzeneMessage(message => message.UseMessageHandlers())
                .UseMessageHandlers()));
}
