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

    // DynamoDB Local (Docker Compose) needs an explicit endpoint + throwaway credentials; a real AWS
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

        // The SDK requires some region for SigV4 signing even against DynamoDB Local (which ignores
        // its value) - but setting RegionEndpoint (as earlier revisions of this file did) makes the
        // SDK's DetermineServiceURL() ignore ServiceURL entirely and resolve the real AWS endpoint for
        // that region instead (aws/aws-sdk-net#1781), silently sending every request to real AWS
        // DynamoDB signed with throwaway credentials - the actual cause of the "security token
        // included in the request is invalid" failures seen in CI, not endpoint discovery or
        // credential formatting (both tried and ruled out first). AuthenticationRegion supplies the
        // signing region without affecting endpoint resolution, so ServiceURL is honored.
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = serviceUrl,
            AuthenticationRegion = "us-east-1",
            EndpointDiscoveryEnabled = false
        };
        return new AmazonDynamoDBClient(new BasicAWSCredentials("DUMMYIDEXAMPLE", "DUMMYEXAMPLEKEY"), config);
    }
}
