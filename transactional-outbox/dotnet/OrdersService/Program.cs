using Amazon.DynamoDBv2;
using Benzene.AspNet.Core;
using Benzene.Patterns.TransactionalOutbox.OrdersService;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);
builder.UseBenzene<StartUp>();

var app = builder.Build();

var dynamoDb = app.Services.GetRequiredService<IAmazonDynamoDB>();
var logger = app.Services.GetRequiredService<ILogger<Program>>();

// Two tables, differing only in whether they have a change stream - which is the only difference
// between the reliable path and the unreliable one.
await DynamoDbTableProvisioning.EnsureOrdersTableExistsAsync(
    dynamoDb, app.Configuration["ORDERS_TABLE_NAME"] ?? "orders", streamEnabled: true, logger);
await DynamoDbTableProvisioning.EnsureOrdersTableExistsAsync(
    dynamoDb, app.Configuration["NAIVE_ORDERS_TABLE_NAME"] ?? "orders-naive", streamEnabled: false, logger);

app.UseBenzene();
app.Run();
