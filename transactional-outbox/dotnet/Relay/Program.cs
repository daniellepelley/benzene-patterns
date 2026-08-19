using Amazon.DynamoDBv2;
using Benzene.AspNet.Core;
using Benzene.Clients;
using Benzene.Clients.Http;
using Benzene.Core.MessageHandlers.DI;
using Benzene.Microsoft.Dependencies;
using Benzene.Patterns.TransactionalOutbox.Contracts;
using Benzene.Patterns.TransactionalOutbox.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

// The relay is a background worker, not a request handler - it has no inbound surface of its own,
// which is why this Program.cs configures Benzene's OUTBOUND half only and then hosts a
// BackgroundService. Its one job is to turn committed changes into published events.
var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var notificationsUrl = configuration["NOTIFICATIONS_URL"] ?? "http://notifications:8080/benzene-message";

builder.Services.AddSingleton(_ => DynamoDbClientFactory.Create(configuration));
builder.Services.AddSingleton<IAmazonDynamoDBStreams>(_ =>
{
    var config = DynamoDbClientFactory.LocalConfig(configuration);
    return config is null
        ? new AmazonDynamoDBStreamsClient()
        : new AmazonDynamoDBStreamsClient(DynamoDbClientFactory.LocalCredentials,
            new AmazonDynamoDBStreamsConfig
            {
                ServiceURL = config.ServiceURL,
                AuthenticationRegion = config.AuthenticationRegion
            });
});
builder.Services.AddSingleton(_ => new HttpClient());
builder.Services.AddHostedService<OrderStreamRelay>();

builder.Services.UsingBenzene(x => x
    // AddBenzene() explicitly, because this service has no INBOUND pipeline. Every other service
    // here gets the baseline registrations - the default ISerializer among them - as a side effect
    // of building one; a pure outbound worker has to ask. Without it the first publish fails at
    // runtime resolving ISerializer, which is how this line came to be written.
    .AddBenzene()
    .AddOutboundRouting(routing => routing
        .Route(Topics.OrderCreated, p => p.UseBenzeneMessageOverHttp(notificationsUrl))));

var app = builder.Build();
// A liveness surface so compose and CI can tell the relay is up. Deliberately trivial: the relay's
// real work is the stream loop, not this.
app.MapGet("/", () => "relay: reading the orders stream");
app.Run();
