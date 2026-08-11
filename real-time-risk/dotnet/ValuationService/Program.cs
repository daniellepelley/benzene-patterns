using Benzene.AspNet.Core;
using Benzene.Patterns.RealTimeRisk.ValuationService;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// Run StartUp.GetConfiguration + StartUp.ConfigureServices, stashing StartUp for the call below.
// The Kafka consumer is registered from inside ConfigureServices (see StartUp and
// KafkaWorkerHosting), not here, because it needs the pre-Build IServiceCollection to register its
// pipelines into - unlike RiskReadModels' projector, which is a plain BackgroundService with nothing
// to register.
builder.UseBenzene<StartUp>();

var app = builder.Build();

// Run StartUp.Configure against the built pipeline, wiring Benzene into the request pipeline.
app.UseBenzene();

app.Run();
