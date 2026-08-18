using Benzene.HostedService;
using Benzene.Patterns.Cqrs.ReadModel;
using Microsoft.Extensions.Hosting;

// The queue and its binding first - the worker assumes the queue it consumes exists, and declaring
// it here also makes start-up wait for a reachable broker.
await Topology.DeclareAsync(
    Environment.GetEnvironmentVariable("RABBIT_HOST") ?? "rabbitmq",
    int.TryParse(Environment.GetEnvironmentVariable("RABBIT_PORT"), out var port) ? port : 5672,
    Environment.GetEnvironmentVariable("RABBIT_USER") ?? "guest",
    Environment.GetEnvironmentVariable("RABBIT_PASSWORD") ?? "guest");

// A plain generic host: both transports are workers, so nothing here is ASP.NET-shaped.
var host = Host.CreateDefaultBuilder(args)
    .UseBenzene<StartUp>()
    .Build();

await host.RunAsync();
