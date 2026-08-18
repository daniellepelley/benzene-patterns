using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
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

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseBenzeneMessage(message => message
                .UseMessageHandlers()));
}
