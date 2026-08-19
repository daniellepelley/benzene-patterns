using System.Text;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Core.Middleware;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;
using RabbitMQ.Client;

namespace Benzene.Patterns.Cqrs.Write.UserService;

/// <summary>
/// A terminal outbound middleware that publishes an event to a RabbitMQ exchange.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same missing overload the choreography example writes up, carried again.</b> <c>Benzene.RabbitMq</c> ships the whole outbound path — a context converter, a publish
/// middleware, and a <c>UseRabbitMq&lt;T&gt;()</c> extension — but that extension is written against
/// the older <c>IBenzeneClientContext&lt;T, Void&gt;</c> shape, and the outbound routing table's
/// pipelines are <c>IMiddlewarePipelineBuilder&lt;OutboundContext&gt;</c>. Every cloud transport has
/// both overloads (SQS, SNS, EventBridge, Service Bus, Event Grid, Event Hub, Queue Storage,
/// Pub/Sub, in-process); RabbitMQ, Kafka and HTTP have only the older one. So a route cannot reach
/// RabbitMQ without this file. Two write services here each need one, which takes this repo's count
/// of hand-rolled outbound adapters to EIGHT across six of its eight patterns — the argument for
/// closing the gap upstream, not for getting better at copying the file. The template already
/// exists: <c>Benzene.Clients.Aws.Sqs</c>'s <c>UseSqs(OutboundContext)</c> is an
/// <c>IContextConverter&lt;OutboundContext, SqsSendMessageContext&gt;</c> handed to
/// <c>Convert(...)</c>, and SNS and EventBridge ship the same pair.
/// </para>
/// <para>
/// The wire format is deliberately identical to what <c>RabbitMqContextConverter</c> produces, so a
/// <c>RabbitMqWorker</c> on the other side consumes it with no special configuration: the topic goes
/// on the <c>topic</c> header <em>and</em> the routing key, and the Benzene header dictionary is
/// forwarded UTF-8 encoded. That last part is what carries correlation id and <c>traceparent</c>
/// across the hop, which is what lets the mesh derive the choreography graph from real traffic.
/// </para>
/// </remarks>
public class RabbitMqPublishMiddleware : IMiddleware<OutboundContext>, ITerminalMiddleware
{
    private readonly IChannel _channel;
    private readonly string _exchange;
    private readonly ISerializer _serializer;

    public RabbitMqPublishMiddleware(IChannel channel, string exchange, ISerializer serializer)
    {
        _channel = channel;
        _exchange = exchange;
        _serializer = serializer;
    }

    public string Name => nameof(RabbitMqPublishMiddleware);

    public async Task HandleAsync(OutboundContext context, Func<Task> next)
    {
        var headers = new Dictionary<string, object?>();
        foreach (var header in context.Headers)
        {
            headers[header.Key] = Encoding.UTF8.GetBytes(header.Value ?? string.Empty);
        }

        // The topic travels as a header as well as the routing key. A fanout exchange ignores the
        // routing key entirely, so on this exchange the header is the only thing that tells a
        // consumer what the message IS - which is exactly why Benzene's consumer reads the header
        // first and treats the routing key as the fallback.
        headers["topic"] = Encoding.UTF8.GetBytes(context.Topic);

        var properties = new BasicProperties { Headers = headers, Persistent = true };
        var body = Encoding.UTF8.GetBytes(_serializer.Serialize(context.Request));

        await _channel.BasicPublishAsync(_exchange, context.Topic, mandatory: false, properties, body);

        // Fire-and-forget: accepted by the broker, and that is the whole of what the emitter gets to
        // know. There is no reply, no consumer count, and no way to learn whether anything reacted -
        // which is the point of choreographing rather than orchestrating, not a limitation of this
        // adapter.
        //
        // An already-typed IBenzeneResult<Void>, not a raw envelope. A raw one would send the caller
        // down DefaultBenzeneMessageSender's deserialize-the-body path, and an event publish has no
        // body to deserialize - which surfaces as "the input does not contain any JSON tokens" AFTER
        // the message was successfully published. Same thing the shipped RabbitMqContextConverter
        // does in its MapResponseAsync.
        context.Response = BenzeneResult.Accepted<Void>();

        // Terminal: no next(). Declared via ITerminalMiddleware so Benzene's start-up checks pass -
        // they refuse to boot a pipeline that has no terminal middleware.
    }
}

/// <summary>Binds <see cref="RabbitMqPublishMiddleware"/> into an outbound route.</summary>
public static class RabbitMqOverOutboundExtensions
{
    public static IMiddlewarePipelineBuilder<OutboundContext> UseRabbitMqExchange(
        this IMiddlewarePipelineBuilder<OutboundContext> app, string exchange)
    {
        return app.Use(resolver => new RabbitMqPublishMiddleware(
            resolver.GetService<IChannel>(), exchange, resolver.GetService<ISerializer>()));
    }
}
