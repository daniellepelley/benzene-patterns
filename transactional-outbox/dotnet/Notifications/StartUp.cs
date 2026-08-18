using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Http.BenzeneMessage;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.TransactionalOutbox.Notifications;

/// <summary>
/// The downstream consumer. Serves both an HTTP route (for a reader) and a BenzeneMessage endpoint
/// (for the relay to publish to by topic).
/// </summary>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<NotificationStore>();
        services.UsingBenzene(x => x.AddMessageHandlers(typeof(OrderCreatedHandler).Assembly));
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseBenzeneMessage(message => message.UseMessageHandlers())
            .UseMessageHandlers());
}
