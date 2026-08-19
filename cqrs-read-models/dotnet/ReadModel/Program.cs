using Benzene.HostedService;
using Benzene.Patterns.Cqrs.ReadModel;

// The queue and its binding first - the worker assumes the queue it consumes exists, and declaring
// it here also makes start-up wait for a reachable broker.
await Topology.DeclareAsync(
    Environment.GetEnvironmentVariable("RABBIT_HOST") ?? "rabbitmq",
    int.TryParse(Environment.GetEnvironmentVariable("RABBIT_PORT"), out var port) ? port : 5672,
    Environment.GetEnvironmentVariable("RABBIT_USER") ?? "guest",
    Environment.GetEnvironmentVariable("RABBIT_PASSWORD") ?? "guest");

// Then the host. Both transports are workers declared in StartUp, so nothing here is ASP.NET-shaped.
// BenzeneHost.RunAsync is exactly Host.CreateDefaultBuilder(args).UseBenzene<StartUp>().Build()
// .RunAsync() - the explicit form is one rung down and stays available.
await BenzeneHost.RunAsync<StartUp>(args);
