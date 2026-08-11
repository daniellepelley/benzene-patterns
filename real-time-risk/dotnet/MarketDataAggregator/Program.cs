using Benzene.HostedService;
using Benzene.Patterns.RealTimeRisk.MarketDataAggregator;
using Microsoft.Extensions.Hosting;

// A pure worker, not an ASP.NET app: this service has no HTTP surface, and UseKafkaStream is added
// through IBenzeneWorkerStartup, which only the generic-host UseBenzene wires up (StartUp.Configure's
// app.UseWorker(...) is a no-op on any other application builder). Benzene.HostedService runs
// StartUp.GetConfiguration + ConfigureServices + Configure and registers the resulting worker as an
// IHostedService.
await Host.CreateDefaultBuilder(args)
    .UseBenzene<StartUp>()
    .Build()
    .RunAsync();
