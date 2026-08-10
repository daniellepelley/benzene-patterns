using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Builds this service's two DynamoDB clients (table + streams) against the same endpoint override,
/// so the DynamoDB-Local-vs-real-AWS decision (see <see cref="StartUp"/>) is made once, not twice.
/// </summary>
internal static class DynamoDbClients
{
    // DynamoDB Local 2.0+ validates the access key format more strictly than earlier versions - a
    // short placeholder like "local" is rejected even though it's alphanumeric. This is the exact
    // placeholder AWS's own DynamoDB Local docs use.
    private static readonly BasicAWSCredentials LocalCredentials = new("DUMMYIDEXAMPLE", "DUMMYEXAMPLEKEY");

    public static IAmazonDynamoDB CreateTableClient(IConfiguration configuration)
    {
        var serviceUrl = configuration["DYNAMODB_SERVICE_URL"];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonDynamoDBClient();
        }

        // The SDK requires some region even against DynamoDB Local (which ignores its value) - set it
        // explicitly rather than depending on an AWS_REGION env var the compose file would otherwise
        // have to remember to set. Endpoint discovery is explicitly disabled - see TradeLedger's
        // StartUp.cs comment for why (it was the actual cause of a "security token invalid" CI failure
        // that the credential-format fix alone didn't resolve).
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = serviceUrl,
            RegionEndpoint = RegionEndpoint.USEast1,
            EndpointDiscoveryEnabled = false
        };
        return new AmazonDynamoDBClient(LocalCredentials, config);
    }

    public static IAmazonDynamoDBStreams CreateStreamsClient(IConfiguration configuration)
    {
        // DynamoDB Local serves the Streams API on the same endpoint as the table API.
        var serviceUrl = configuration["DYNAMODB_SERVICE_URL"];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonDynamoDBStreamsClient();
        }

        var config = new AmazonDynamoDBStreamsConfig { ServiceURL = serviceUrl, RegionEndpoint = RegionEndpoint.USEast1 };
        return new AmazonDynamoDBStreamsClient(LocalCredentials, config);
    }
}
