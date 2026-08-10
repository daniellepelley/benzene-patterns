using Benzene.AspNet.Core;
using Benzene.Patterns.RealTimeRisk.RiskReadModels;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// Run StartUp.GetConfiguration + StartUp.ConfigureServices, stashing StartUp for the call below.
builder.UseBenzene<StartUp>();

// The projector is plain ASP.NET Core hosting, not a Benzene-routed message: it never receives an
// inbound request, it just runs continuously alongside the HTTP server. Registered directly on the
// same underlying IServiceCollection StartUp.ConfigureServices wires Benzene into, so it resolves the
// same singletons (IAmazonDynamoDB, IAmazonDynamoDBStreams, BookPositionsStore).
builder.Services.AddHostedService<TradeStreamProjector>();

var app = builder.Build();

// Run StartUp.Configure against the built pipeline, wiring Benzene into the request pipeline.
app.UseBenzene();

app.Run();
