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

        // The SDK requires some region even against DynamoDB Local (which ignores its value) - set it
        // explicitly rather than depending on an AWS_REGION env var the compose file would otherwise
        // have to remember to set. The access key uses AWS's own documented DynamoDB Local placeholder
        // (alphanumeric, long enough to pass 2.0+'s stricter format validation). Endpoint discovery is
        // explicitly disabled: the CI failure's stack trace showed EndpointDiscoveryHandler running
        // before every request even with ServiceURL set, making a separate discovery call the SDK
        // wasn't pointing at DynamoDB Local, and *that* call was the one DynamoDB Local (or whatever it
        // actually reached) rejected with "security token invalid" - not the real table request.
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = serviceUrl,
            RegionEndpoint = RegionEndpoint.USEast1,
            EndpointDiscoveryEnabled = false
        };
        return new AmazonDynamoDBClient(new BasicAWSCredentials("DUMMYIDEXAMPLE", "DUMMYEXAMPLEKEY"), config);
    }
}
