using System.Collections.Concurrent;
using System.Text.Json;
using Benzene.Patterns.Cqrs.Contracts;

namespace Benzene.Patterns.Cqrs.Write.UserService;

/// <summary>
/// Every event this service has emitted, in order, so a read model can be rebuilt from it.
/// </summary>
/// <remarks>
/// <para>
/// <b>A read model is only rebuildable if something durable remembers the events.</b> A fanout
/// exchange does not: it delivers to the queues bound at publish time and keeps nothing, so replaying
/// from the broker is not an option. The replay source has to be on the write side, next to the data
/// that produced it.
/// </para>
/// <para>
/// In production this is the <a href="../../transactional-outbox/README.md">outbox table</a> — the
/// same rows the relay publishes from — or a durable log (Kinesis, Kafka, an event store). This
/// in-memory list stands in for it so the rebuild is demonstrable on a laptop, and it is the one
/// piece of this example that a real deployment would replace outright.
/// </para>
/// </remarks>
public class EventLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentQueue<RecordedEvent> _events = new();
    private long _sequence;

    public void Append(string topic, object body) => _events.Enqueue(new RecordedEvent
    {
        Topic = topic,
        Body = JsonSerializer.Serialize(body, body.GetType(), Json),
        Sequence = Interlocked.Increment(ref _sequence)
    });

    public EventLogResponse Read()
    {
        var events = _events.OrderBy(x => x.Sequence).ToList();
        return new EventLogResponse { Count = events.Count, Events = events };
    }
}
