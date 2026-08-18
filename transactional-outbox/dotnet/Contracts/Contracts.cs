namespace Benzene.Patterns.TransactionalOutbox.Contracts;

/// <summary>The topics this example's services address each other by.</summary>
public static class Topics
{
    /// <summary>Command: place an order. Handled by the Orders service.</summary>
    public const string OrderPlace = "order:place";

    /// <summary>
    /// Event: an order was placed. Published by the <b>relay</b>, never by the Orders service.
    /// </summary>
    /// <remarks>
    /// That is the whole pattern in one sentence. The service that owns the data does not publish;
    /// the event is a consequence of the committed write, emitted by something reading the change
    /// stream. There is no window in which the order exists and the event might not.
    /// </remarks>
    public const string OrderCreated = "order:created";
}

public class PlaceOrderRequest
{
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }

    /// <summary>
    /// Only honoured by the <c>/orders/naive</c> endpoint: crash after committing, before publishing.
    /// </summary>
    /// <remarks>
    /// A switch that exists to make a bug reproducible. The naive endpoint is in this example
    /// precisely so the dual-write problem can be OBSERVED - an order that committed and an event
    /// that will never arrive - rather than taken on trust from a paragraph.
    /// </remarks>
    public bool CrashBeforePublish { get; set; }
}

public class PlaceOrderResponse
{
    public string OrderId { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }

    /// <summary>Which path wrote this order — <c>cdc</c> or <c>naive</c>.</summary>
    public string Path { get; set; } = string.Empty;
}

/// <summary>The event the relay publishes, built from the committed row.</summary>
public class OrderCreated
{
    public string OrderId { get; set; } = string.Empty;
    public string Customer { get; set; } = string.Empty;
    public decimal Total { get; set; }

    /// <summary>
    /// The DynamoDB stream record's sequence number, carried as the event's identity.
    /// </summary>
    /// <remarks>
    /// The consumer dedupes on this. An outbox guarantees at-least-once EMISSION and deliberately
    /// not exactly-once - that is not achievable across systems - so "each event takes effect once"
    /// is completed on the consumer side. Using the stream's own sequence number means a redelivery
    /// after a failed publish carries the same identity as the original.
    /// </remarks>
    public string EventId { get; set; } = string.Empty;
}

public class NotificationsResponse
{
    public int Count { get; set; }

    /// <summary>How many deliveries were recognised as repeats and ignored.</summary>
    public int Duplicates { get; set; }

    public List<OrderCreated> Notifications { get; set; } = new();
}
