using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Benzene.Abstractions.Hosting;
using Benzene.AspNet.Core;
using Benzene.Core.MessageHandlers;
using Benzene.Core.MessageHandlers.DI;
using Benzene.EventSourcing.DynamoDb;
using Benzene.Microsoft.Dependencies;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Benzene.Patterns.RealTimeRisk.TradeLedger;

/// <summary>
/// The Trade Ledger from docs/patterns/reference-real-time-risk.md §5 - the book of record. Every
/// booked trade is a command handler appending an immutable event to a DynamoDB-backed event log
/// (Benzene.EventSourcing.DynamoDb's DynamoDbEventStore). Hosted as a plain ASP.NET container for this
/// local/Docker Compose slice - see real-time-risk/README.md for why (no Lambda emulation needed for
/// a service with no event-source trigger of its own).
/// </summary>
public class StartUp : BenzeneStartUp
{
    public override IConfiguration GetConfiguration()
        => new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .Build();

    public override void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        => services.UsingBenzene(x => x
            .AddSingleton<IAmazonDynamoDB>(_ => CreateDynamoDbClient(configuration))
            .AddDynamoDbEventStore(configuration["TRADES_TABLE_NAME"] ?? "trades")
            .AddMessageHandlers(typeof(BookTradeHandler).Assembly));

    public override void Configure(IBenzeneApplicationBuilder app, IConfiguration configuration)
        => app.UseHttp(http => http
            .UseMessageHandlers());

    // LocalStack (Docker Compose) needs an explicit endpoint + throwaway credentials; a real AWS
    // deployment would omit DYNAMODB_SERVICE_URL and fall back to the default client (region +
    // credential chain from the environment/IAM role) - not exercised by this local-first slice yet,
    // see real-time-risk/README.md's roadmap.
    private static IAmazonDynamoDB CreateDynamoDbClient(IConfiguration configuration)
    {
        var serviceUrl = configuration["DYNAMODB_SERVICE_URL"];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonDynamoDBClient();
        }

        // The SDK requires some region even against LocalStack (which ignores its value) - set it
        // explicitly rather than depending on an AWS_REGION env var the compose file would otherwise
        // have to remember to set.
        var config = new AmazonDynamoDBConfig { ServiceURL = serviceUrl, RegionEndpoint = RegionEndpoint.USEast1 };
        return new AmazonDynamoDBClient(new BasicAWSCredentials("local", "local"), config);
    }
}
