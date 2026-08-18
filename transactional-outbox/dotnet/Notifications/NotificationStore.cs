using System.Collections.Concurrent;
using Benzene.Patterns.TransactionalOutbox.Contracts;

namespace Benzene.Patterns.TransactionalOutbox.Notifications;

/// <summary>
/// The consumer's state, and the other half of the reliability contract.
/// </summary>
/// <remarks>
/// <para>
/// An outbox guarantees at-least-once <b>emission</b>. It deliberately does not guarantee
/// exactly-once, because that is not achievable across two systems. "Each event takes effect once"
/// is finished here, on the consumer, by making the reaction idempotent.
/// </para>
/// <para>
/// In production this is <c>Benzene.Idempotency</c>'s <c>UseIdempotency()</c> over a distributed
/// store - a DynamoDB conditional write, a Redis <c>SET NX</c>. An in-memory set is the single-replica
/// stand-in; the DISCIPLINE is the same and is what the pattern asks for.
/// </para>
/// </remarks>
public class NotificationStore
{
    private readonly ConcurrentDictionary<string, OrderCreated> _byEventId = new();
    private int _duplicates;

    /// <summary>Records a delivery. Returns false when this event has already taken effect.</summary>
    public bool Record(OrderCreated notification)
    {
        if (_byEventId.TryAdd(notification.EventId, notification))
        {
            return true;
        }

        Interlocked.Increment(ref _duplicates);
        return false;
    }

    public NotificationsResponse Snapshot() => new()
    {
        Count = _byEventId.Count,
        Duplicates = _duplicates,
        Notifications = _byEventId.Values.OrderBy(x => x.OrderId, StringComparer.Ordinal).ToList()
    };
}
