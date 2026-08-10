using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.RealTimeRisk.TradeLedger;

/// <summary>
/// Creates the ledger's DynamoDB table (with its stream enabled) at startup if it doesn't already
/// exist. DynamoDB Local (the Docker Compose local-dev target - see docker-compose.yml) has no
/// bundled provisioning tool and no separate init step in this repo, so the service that owns the
/// table provisions it itself, idempotently, with retries for the window right after `docker compose
/// up` when the database container may not have finished starting yet.
/// </summary>
internal static class DynamoDbTableProvisioning
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Composite key matching Benzene.EventSourcing.DynamoDb's DynamoDbEventStore defaults.</summary>
    public static async Task EnsureTradesTableExistsAsync(
        IAmazonDynamoDB dynamoDb, string tableName, ILogger logger, CancellationToken cancellationToken = default)
    {
        var request = new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions =
            [
                new AttributeDefinition("pk", ScalarAttributeType.S),
                new AttributeDefinition("version", ScalarAttributeType.N)
            ],
            KeySchema =
            [
                new KeySchemaElement("pk", KeyType.HASH),
                new KeySchemaElement("version", KeyType.RANGE)
            ],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification
            {
                StreamEnabled = true,
                StreamViewType = StreamViewType.NEW_AND_OLD_IMAGES
            }
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await dynamoDb.CreateTableAsync(request, cancellationToken);
                logger.LogInformation("Created the {Table} table (streams enabled).", tableName);
                return;
            }
            catch (ResourceInUseException)
            {
                // Already provisioned - a previous run, or this process restarting. Nothing to do.
                logger.LogInformation("The {Table} table already exists.", tableName);
                return;
            }
            catch (Exception ex) when (attempt < MaxAttempts)
            {
                logger.LogInformation(ex, "Could not provision the {Table} table yet (attempt {Attempt}/{MaxAttempts}) - retrying.",
                    tableName, attempt, MaxAttempts);
                await Task.Delay(RetryInterval, cancellationToken);
            }
            // The final attempt (attempt == MaxAttempts) is excluded by the `when` guard above, so its
            // exception propagates to the caller instead of being retried or swallowed.
        }
    }
}
