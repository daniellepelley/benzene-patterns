using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.TwoTier.Core.UserService;

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
        services.AddSingleton<UserStore>();
        services.UsingBenzene(x => x.AddMessageHandlers(typeof(UserStore).Assembly));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseBenzeneMessage(message => message.UseMessageHandlers())
            .UseMessageHandlers());
}
