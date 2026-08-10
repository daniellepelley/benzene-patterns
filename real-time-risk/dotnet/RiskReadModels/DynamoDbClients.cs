using Amazon;
using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Builds this service's two DynamoDB clients (table + streams) against the same endpoint override,
/// so the LocalStack-vs-real-AWS decision (see <see cref="StartUp"/>) is made once, not twice.
/// </summary>
internal static class DynamoDbClients
{
    public static IAmazonDynamoDB CreateTableClient(IConfiguration configuration)
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

    public static IAmazonDynamoDBStreams CreateStreamsClient(IConfiguration configuration)
    {
        // LocalStack serves DynamoDB Streams on the same endpoint as the table API.
        var serviceUrl = configuration["DYNAMODB_SERVICE_URL"];
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return new AmazonDynamoDBStreamsClient();
        }

        var config = new AmazonDynamoDBStreamsConfig { ServiceURL = serviceUrl, RegionEndpoint = RegionEndpoint.USEast1 };
        return new AmazonDynamoDBStreamsClient(new BasicAWSCredentials("local", "local"), config);
    }
}
