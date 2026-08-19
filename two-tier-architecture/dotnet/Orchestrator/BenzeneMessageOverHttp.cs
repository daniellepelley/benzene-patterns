using System.Net.Http.Json;
using System.Text.Json;
using Benzene.Abstractions.DI;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;

namespace Benzene.Patterns.TwoTier.Orchestrator;

/// <summary>
/// A terminal outbound middleware that POSTs the BenzeneMessage envelope
/// (<c>{ topic, headers, body }</c>) to another Benzene service's <c>/benzene-message</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the one piece of framework plumbing this repo hand-rolls, and it is worth saying why.</b>
/// The outbound routing table binds a topic to a transport middleware — <c>UseSqs</c>,
/// <c>UseServiceBus</c>, <c>UseInProcess</c>, and in production for this pattern a Lambda invoke.
/// <c>Benzene.Clients.Http</c> ships <c>HttpBenzeneMessageClient</c>, which is documented as exactly
/// "the HTTP counterpart of the AWS Lambda invoke path", but it is registered as an
/// <c>IBenzeneMessageClient</c> and there is no <c>UseBenzeneMessageOverHttp()</c> extension on
/// <c>OutboundContext</c> to bind it into a route — so a route cannot reach it without this adapter.
/// That gap is the only reason this file exists; it is a note for the framework, not a design choice
/// of the example. <b>Counted across this repo, this gap has been hand-filled eight times</b> — five
/// copies of this HTTP adapter (two-tier orchestrator, modular-monolith extraction,
/// transactional-outbox orders service AND relay, real-time-risk map-reduce coordinator) and three of
/// its RabbitMQ twin (choreography emitter, both CQRS write services): six of the eight patterns,
/// ~750 lines, differing only in namespace. That count is the argument for closing it upstream and
/// deleting every copy.
/// </para>
/// <para>
/// It uses a documented seam rather than a private one: <c>DefaultBenzeneMessageSender</c> explicitly
/// supports a transport that cannot produce a typed result — it does not know <c>TResponse</c>, only
/// the caller does — leaving the raw <see cref="BenzeneMessageClientResponse"/> on the context to be
/// deserialized once the type is known. That is precisely what this does.
/// </para>
/// </remarks>
public class BenzeneMessageOverHttpMiddleware : IMiddleware<OutboundContext>, ITerminalMiddleware
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _url;
    private readonly ISerializer _serializer;

    public BenzeneMessageOverHttpMiddleware(HttpClient http, string url, ISerializer serializer)
    {
        _http = http;
        _url = url;
        _serializer = serializer;
    }

    public string Name => nameof(BenzeneMessageOverHttpMiddleware);

    public async Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        var envelope = new
        {
            topic = context.Topic,
            headers = context.Headers,
            body = _serializer.Serialize(context.Request)
        };

        var response = await _http.PostAsJsonAsync(_url, envelope, Json).ConfigureAwait(false);
        var payload = await response.Content.ReadFromJsonAsync<Envelope>(Json).ConfigureAwait(false);

        // A transport failure and a mapped non-2xx are different things. The serving side maps the
        // Benzene status onto the HTTP status as well as carrying it in the envelope, so a 404 for a
        // NotFound is a normal RESULT and must be read from the body - only a missing or unparseable
        // envelope is genuinely a transport problem.
        // The optional `isSuccessful` argument is left unset on purpose. Benzene.Clients 0.0.3-alpha.1
        // does carry it, and AsBenzeneResult reads it as `source.IsSuccessful ?? IsSuccessStatus(status)`
        // - so omitting it takes the documented fallback, which classifies the status against the known
        // vocabulary and is exact for every status this worker returns. Set it only for a transport
        // that knows something the status string does not.
        context.Response = payload is null
            ? new BenzeneMessageClientResponse("service-unavailable", string.Empty)
            : new BenzeneMessageClientResponse(payload.StatusCode, payload.Body ?? string.Empty, payload.Headers);

        // Terminal: no next(). Same shape as every other transport middleware - and declared as such
        // via ITerminalMiddleware, which is not optional bookkeeping: Benzene's start-up checks refuse
        // to boot a pipeline that has no terminal middleware, because a message reaching the end of one
        // unhandled would otherwise fail silently on the message path much later. It caught exactly
        // that here on the first run.
    }

    private sealed record Envelope(string StatusCode, string? Body, Dictionary<string, string>? Headers);
}

/// <summary>Binds <see cref="BenzeneMessageOverHttpMiddleware"/> into an outbound route.</summary>
public static class BenzeneMessageOverHttpExtensions
{
    public static IMiddlewarePipelineBuilder<OutboundContext> UseBenzeneMessageOverHttp(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string url)
    {
        return app.Use(resolver => new BenzeneMessageOverHttpMiddleware(
            resolver.GetService<HttpClient>(), url, resolver.GetService<ISerializer>()));
    }
}
