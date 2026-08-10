using Amazon.DynamoDBv2;
using Benzene.AspNet.Core;
using Benzene.Patterns.RealTimeRisk.TradeLedger;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Run StartUp.GetConfiguration + StartUp.ConfigureServices, stashing StartUp for the call below.
builder.UseBenzene<StartUp>();

var app = builder.Build();

// This service owns the ledger table, so it provisions it (idempotently, with retries for the
// window right after `docker compose up`) before accepting any requests - see
// DynamoDbTableProvisioning and docker-compose.yml's comment on why there's no separate init step.
await DynamoDbTableProvisioning.EnsureTradesTableExistsAsync(
    app.Services.GetRequiredService<IAmazonDynamoDB>(),
    app.Configuration["TRADES_TABLE_NAME"] ?? "trades",
    app.Services.GetRequiredService<ILogger<Program>>());

// Run StartUp.Configure against the built pipeline, wiring Benzene into the request pipeline.
app.UseBenzene();

app.Run();
