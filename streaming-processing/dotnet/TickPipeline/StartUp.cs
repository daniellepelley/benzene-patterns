using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.Streaming.TickPipeline;

/// <summary>
/// One service: a producer endpoint, a shard, and a real Benzene stream pipeline over it.
/// </summary>
/// <remarks>
/// <para>
/// The stream pipeline is built ONCE here and shared by every invocation, which is how a transport
/// binding does it too — <c>UseKinesisStream</c> builds its pipeline at start-up and runs it per
/// batch. Per-run options ride on the <c>StreamContext</c>'s metadata rather than being baked into
/// the closure.
/// </para>
/// <para>
/// Note what is NOT registered: no message-handler pipeline over the ticks. That is the whole
/// fan-in/fan-out decision. A per-message transport would give each tick its own pipeline invocation
/// and process them concurrently — which is right for independent work items and wrong here, because
/// order and cross-record aggregation are the problem.
/// </para>
/// </remarks>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder().AddEnvironmentVariables().Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        var shard = new Shard();
        var bars = new BarStore();
        var pills = new PoisonPills();

        services.AddSingleton(shard);
        services.AddSingleton(bars);
        services.AddSingleton(pills);
        services.AddScoped<StreamProcessor>();

        services.UsingBenzene(x =>
        {
            x.AddMessageHandlers(typeof(PublishTicksHandler).Assembly);
            x.AddSingleton(StreamProcessor.BuildPipeline(x, bars, pills));
        });
    }

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http.UseMessageHandlers());
}
