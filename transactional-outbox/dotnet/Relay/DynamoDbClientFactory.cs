using Amazon.DynamoDBv2;
using Amazon.Runtime;
using Microsoft.Extensions.Configuration;

namespace Benzene.Patterns.TransactionalOutbox.Relay;

/// <summary>
/// Builds a DynamoDB client for either DynamoDB Local or a real AWS account.
/// </summary>
public static class DynamoDbClientFactory
{
    public static AmazonDynamoDBConfig? LocalConfig(IConfiguration configuration)
    {
        var serviceUrl = configuration["DYNAMODB_SERVICE_URL"];
        return string.IsNullOrEmpty(serviceUrl) ? null : new AmazonDynamoDBConfig
        {
            ServiceURL = serviceUrl,
            // AuthenticationRegion, NOT RegionEndpoint. Setting RegionEndpoint makes the SDK's
            // endpoint resolution ignore ServiceURL entirely and send every request to real AWS
            // signed with throwaway credentials (aws/aws-sdk-net#1781) - which surfaces as an
            // "invalid security token" error that looks nothing like its cause. Learned the hard way
            // in the real-time-risk example's CI; the same note lives in that service's StartUp.
            AuthenticationRegion = "us-east-1",
            EndpointDiscoveryEnabled = false
        };
    }

    public static AWSCredentials LocalCredentials => new BasicAWSCredentials("DUMMYIDEXAMPLE", "DUMMYEXAMPLEKEY");

    public static IAmazonDynamoDB Create(IConfiguration configuration)
    {
        var config = LocalConfig(configuration);
        return config is null ? new AmazonDynamoDBClient() : new AmazonDynamoDBClient(LocalCredentials, config);
    }
}
