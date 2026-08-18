using Benzene.HostedService;
using Benzene.Patterns.Choreography.Reactions.TrialClock;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The queue and its binding are declared BEFORE the host starts, because the worker assumes the
// queue it consumes already exists (as every Benzene queue worker does - declaring topology is not
// the transport package's job). Doing it here also makes the start-up wait for a reachable broker.
await Topology.DeclareAsync(
    Environment.GetEnvironmentVariable("RABBIT_HOST") ?? "rabbitmq",
    int.TryParse(Environment.GetEnvironmentVariable("RABBIT_PORT"), out var port) ? port : 5672,
    Environment.GetEnvironmentVariable("RABBIT_USER") ?? "guest",
    Environment.GetEnvironmentVariable("RABBIT_PASSWORD") ?? "guest");

var builder = WebApplication.CreateBuilder(args);

// The Benzene RabbitMQ consumer runs as a hosted service on the generic host. The web server beside
// it exists ONLY so a reader (and the smoke test) can see what this reaction actually did - it is a
// window onto the journal, not part of the pattern.
builder.Host.UseBenzene<StartUp>();

var app = builder.Build();
app.MapGet("/trials", (Journal journal) => journal.Read());
app.Run();
