using Amazon.DynamoDBv2;
using Benzene.HostedService;
using Benzene.Patterns.TransactionalOutbox.OrdersService;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// BenzeneHost.Build rather than RunAsync: this service provisions its tables before it starts
// serving, so it needs the host in hand. That is what Build is for - the shorthand hands back the
// IHost instead of being a dead end the moment a service needs to do anything before it runs.
var host = BenzeneHost.Build<StartUp>(args);

var dynamoDb = host.Services.GetRequiredService<IAmazonDynamoDB>();
var configuration = host.Services.GetRequiredService<IConfiguration>();
var logger = host.Services.GetRequiredService<ILogger<Program>>();

// Two tables, differing only in whether they have a change stream - which is the only difference
// between the reliable path and the unreliable one.
await DynamoDbTableProvisioning.EnsureOrdersTableExistsAsync(
    dynamoDb, configuration["ORDERS_TABLE_NAME"] ?? "orders", streamEnabled: true, logger);
await DynamoDbTableProvisioning.EnsureOrdersTableExistsAsync(
    dynamoDb, configuration["NAIVE_ORDERS_TABLE_NAME"] ?? "orders-naive", streamEnabled: false, logger);

await host.RunAsync();
