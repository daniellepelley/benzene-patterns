using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TransactionalOutbox.OrdersService;

/// <summary>
/// Creates this example's two order tables at startup if they don't already exist. DynamoDB Local (the Docker Compose local-dev target - see docker-compose.yml) has no
/// bundled provisioning tool and no separate init step in this repo, so the service that owns the
/// table provisions it itself, idempotently, with retries for the window right after `docker compose
/// up` when the database container may not have finished starting yet.
/// </summary>
internal static class DynamoDbTableProvisioning
{
    private const int MaxAttempts = 30;
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Creates one order table, with change capture on or off.
    /// </summary>
    /// <param name="streamEnabled">
    /// The single difference between this example's two paths.
    /// <para>
    /// The CDC table has a stream, so a committed write IS the trigger for the relay. The naive
    /// table has none, so the only thing that can publish an event for it is the handler itself -
    /// which is the dual-write problem, and the reason that table exists here at all: to make the
    /// bug reproducible rather than merely described.
    /// </para>
    /// </param>
    public static async Task EnsureOrdersTableExistsAsync(
        IAmazonDynamoDB dynamoDb, string tableName, bool streamEnabled, ILogger logger, CancellationToken cancellationToken = default)
    {
        var request = new CreateTableRequest
        {
            TableName = tableName,
            AttributeDefinitions = [new AttributeDefinition("orderId", ScalarAttributeType.S)],
            KeySchema = [new KeySchemaElement("orderId", KeyType.HASH)],
            BillingMode = BillingMode.PAY_PER_REQUEST,
            StreamSpecification = new StreamSpecification
            {
                StreamEnabled = streamEnabled,
                StreamViewType = streamEnabled ? StreamViewType.NEW_AND_OLD_IMAGES : null
            }
        };

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            try
            {
                await dynamoDb.CreateTableAsync(request, cancellationToken);
                logger.LogInformation("Created the {Table} table (stream enabled: {StreamEnabled}).", tableName, streamEnabled);
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
