using Benzene.HostedService;
using Benzene.Patterns.Streaming.TickPipeline;
using Microsoft.Extensions.Hosting;

// The plain generic host - nothing ASP.NET-shaped here. StartUp declares HTTP as a transport
// alongside any other it might grow, so this file does not change when that happens.
var host = Host.CreateDefaultBuilder(args)
    .UseBenzene<StartUp>()
    .Build();

await host.RunAsync();
