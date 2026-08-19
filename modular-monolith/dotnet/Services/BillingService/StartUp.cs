using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Benzene.SelfHost;
using Benzene.Patterns.ModularMonolith.Modules.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.ModularMonolith.Services.BillingService;

/// <summary>
/// The Billing module, extracted into its own process.
/// </summary>
/// <remarks>
/// The entire service is this file plus a five-line Program.cs. The module's handlers, store and
/// contracts moved across untouched - they never saw a transport in the monolith and they do not
/// now, which is the write-once-host-anywhere property the pattern leans on. The only new thing is
/// an inbound adapter: a BenzeneMessage endpoint, so a caller can reach it by topic over HTTP
/// exactly as it did in process.
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
    /// <c>UseAspNet</c> binds <c>http://0.0.0.0:8080</c> by default; the <c>options</c> argument
    /// overrides it. This service reads <c>PORT</c> — the variable Cloud Run and Heroku inject, and
    /// the one you need to run two of these on one machine without Docker.
    /// </remarks>
    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseWorker(worker => worker
            .UseAspNet(
                http => http
                    .UseBenzeneMessage(message => message.UseMessageHandlers()),
                options => options.Urls = $"http://0.0.0.0:{configuration["PORT"] ?? "8080"}"));
}
