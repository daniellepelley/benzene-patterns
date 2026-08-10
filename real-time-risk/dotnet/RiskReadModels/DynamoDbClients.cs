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

        // The SDK requires some region for SigV4 signing even against DynamoDB Local (which ignores
        // its value) - but setting RegionEndpoint (as earlier revisions of this file did) makes the
        // SDK's DetermineServiceURL() ignore ServiceURL entirely and resolve the real AWS endpoint for
        // that region instead (aws/aws-sdk-net#1781), silently sending every request to real AWS
        // DynamoDB signed with throwaway credentials - see TradeLedger's StartUp.cs comment for the
        // full story (this was the actual cause of the "security token invalid" CI failures, not
        // endpoint discovery or credential formatting, both tried and ruled out first).
        // AuthenticationRegion supplies the signing region without affecting endpoint resolution.
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = serviceUrl,
            AuthenticationRegion = "us-east-1",
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

        // Same ServiceURL-vs-RegionEndpoint pitfall as CreateTableClient above applies here too.
        var config = new AmazonDynamoDBStreamsConfig { ServiceURL = serviceUrl, AuthenticationRegion = "us-east-1" };
        return new AmazonDynamoDBStreamsClient(LocalCredentials, config);
    }
}
