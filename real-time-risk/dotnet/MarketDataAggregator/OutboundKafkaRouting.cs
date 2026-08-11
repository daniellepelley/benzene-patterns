using System.Text;
using Benzene.Abstractions.Middleware;
using Benzene.Abstractions.Serialization;
using Benzene.Clients;
using Benzene.Kafka.Core.Kafka;
using Benzene.Results;
using Confluent.Kafka;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator;

/// <summary>
/// Lets an <c>AddOutboundRouting(routing =&gt; routing.Route(topic, ...))</c> route publish to Kafka.
/// </summary>
/// <remarks>
/// <para><strong>This is glue that ought to live in the framework, and is only here because it
/// doesn't yet.</strong> Benzene's outbound routing table
/// (<c>Benzene.Clients</c>' <c>AddOutboundRouting</c>/<c>OutboundRoutingBuilder.Route</c>, the
/// mechanism docs/patterns/choreography.md's <c>sender.SendAsync&lt;TenantCreated, Void&gt;(...)</c>
/// example is built on) hands each route an <see cref="OutboundContext"/> pipeline. SNS, SQS,
/// EventBridge, Event Grid, Event Hubs, Queue Storage, Service Bus and Pub/Sub each ship an
/// <c>Outbound...ContextConverter</c> plus a <c>Use&lt;Transport&gt;(this
/// IMiddlewarePipelineBuilder&lt;OutboundContext&gt;)</c> overload for exactly this. Kafka (and
/// RabbitMQ) have not been migrated to that shape: as of Benzene 0.0.2-alpha.4,
/// <c>Benzene.Kafka.Core</c>'s outbound <c>UseKafka&lt;T&gt;</c> still only extends the legacy
/// <c>IBenzeneClientContext&lt;T, Void&gt;</c> pipeline, which nothing in the current clients design
/// can build. So there is no way to route a topic to Kafka today without supplying the missing
/// converter.</para>
/// <para>This file is a deliberately faithful copy of <c>OutboundSnsContextConverter</c>'s shape
/// against <c>KafkaSendMessageContext</c>, reusing <c>Benzene.Kafka.Core</c>'s own
/// <c>KafkaClientMiddleware</c>/<c>UseKafkaClient</c> for the produce itself - nothing about Kafka is
/// reimplemented here, only the <see cref="OutboundContext"/> adapter. It should be raised upstream
/// as <c>Benzene.Kafka.Core</c>'s missing outbound-routing overload and deleted from here when it
/// lands; see real-time-risk/README.md's roadmap note.</para>
/// </remarks>
public class OutboundKafkaContextConverter : IContextConverter<OutboundContext, KafkaSendMessageContext>
{
    private readonly ISerializer _serializer;
    private readonly string? _keyHeader;

    /// <param name="serializer">Serializes the outbound payload onto the record value.</param>
    /// <param name="keyHeader">
    /// The per-call header whose value becomes the Kafka message key, so hash(key) picks the
    /// partition and events sharing a key stay ordered. <c>null</c> produces a keyless (round-robin,
    /// unordered) record. Mirrors <c>KafkaContextConverter</c>'s <c>keyHeader</c> parameter exactly.
    /// </param>
    public OutboundKafkaContextConverter(ISerializer serializer, string? keyHeader = null)
    {
        _serializer = serializer;
        _keyHeader = keyHeader;
    }

    public Task<KafkaSendMessageContext> CreateRequestAsync(OutboundContext contextIn)
    {
        var headers = new Headers();
        foreach (var header in contextIn.Headers)
        {
            // Encoding.UTF8.GetBytes(null) throws, and a null-valued header is a legal dictionary
            // state - coalesce, matching KafkaContextConverter and the inbound getters' convention.
            headers.Add(header.Key, Encoding.UTF8.GetBytes(header.Value ?? string.Empty));
        }

        string? key = null;
        if (_keyHeader is not null)
        {
            contextIn.Headers.TryGetValue(_keyHeader, out key);
        }

        // The Benzene topic IS the Kafka topic on this transport - Benzene's Kafka binding routes a
        // record by its literal Kafka topic name, with no topic attribute to carry a different one
        // (benzene-dotnet docs/getting-started-kafka.md §1.3), which is also why Topics.BarClosed is
        // spelled Kafka-legally. Same call KafkaContextConverter makes on the legacy client path.
        return Task.FromResult(new KafkaSendMessageContext(contextIn.Topic, new Message<string, string>
        {
            Key = key!,
            Value = _serializer.Serialize(contextIn.Request),
            Headers = headers
        }));
    }

    public Task MapResponseAsync(OutboundContext contextIn, KafkaSendMessageContext contextOut)
    {
        contextIn.Response = contextOut.Response?.Status == PersistenceStatus.Persisted
            ? BenzeneResult.Accepted<Void>()
            : BenzeneResult.ServiceUnavailable<Void>("Kafka message was not persisted");
        return Task.CompletedTask;
    }
}

/// <summary>The <see cref="OutboundContext"/> counterpart of <c>Benzene.Kafka.Core</c>'s <c>UseKafka&lt;T&gt;</c>.</summary>
public static class OutboundKafkaExtensions
{
    /// <summary>
    /// Routes this outbound topic to Kafka, producing through <paramref name="producer"/>.
    /// </summary>
    /// <param name="app">The outbound route's pipeline builder.</param>
    /// <param name="producer">The producer to publish with - built and owned by the application, exactly as <c>UseKafkaClient</c> expects.</param>
    /// <param name="keyHeader">The per-call header supplying the Kafka message key (see <see cref="OutboundKafkaContextConverter"/>).</param>
    public static IMiddlewarePipelineBuilder<OutboundContext> UseKafka(
        this IMiddlewarePipelineBuilder<OutboundContext> app,
        IProducer<string, string> producer,
        string? keyHeader = null)
    {
        return app.Convert(
            new OutboundKafkaContextConverter(new Benzene.Core.MessageHandlers.Serialization.JsonSerializer(), keyHeader),
            builder => builder.UseKafkaClient(producer));
    }
}
