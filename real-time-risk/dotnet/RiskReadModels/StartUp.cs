using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Risk Read Models from docs/patterns/reference-real-time-risk.md §3: projects the Trade Ledger's
/// events (consumed off its DynamoDB Stream by <see cref="TradeStreamProjector"/>, a background
/// worker - see that class and real-time-risk/README.md for why this local slice polls directly
/// rather than running as a real Lambda function) into a queryable per-book position view.
/// </summary>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddSingleton<IAmazonDynamoDB>(_ => DynamoDbClients.CreateTableClient(configuration))
            .AddSingleton<IAmazonDynamoDBStreams>(_ => DynamoDbClients.CreateStreamsClient(configuration))
            .AddSingleton<BookPositionsStore>()
            .AddMessageHandlers(typeof(BookPositionsQueryHandler).Assembly));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());
}
