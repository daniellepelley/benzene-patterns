namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>
/// The topic names this pattern's services address each other by. Shared here so the Trade Ledger
/// (producer) and Risk Read Models (consumer) never drift on a topic string.
/// </summary>
public static class Topics
{
    /// <summary>Command: book a trade to the ledger. Handled by the Trade Ledger service.</summary>
    public const string BookTrade = "trade:book";

    /// <summary>Query: current positions for a book. Handled by the Risk Read Models service.</summary>
    public const string BookPositions = "book:positions";

    /// <summary>Query: a price snapshot for one symbol. Handled by the Pricing Service over gRPC.</summary>
    public const string PriceGet = "price:get";

    /// <summary>Query: a live tick stream for one symbol. Handled by the Pricing Service over gRPC.</summary>
    public const string PriceSubscribe = "price:subscribe";

    /// <summary>
    /// Query: a bidirectional price session whose watch list changes while it is open. Handled by the
    /// Pricing Service over gRPC.
    /// </summary>
    public const string PriceStream = "price:stream";

    /// <summary>
    /// The event type discriminator stored on every ledger event (<c>EventEnvelope.EventType</c> /
    /// <c>StoredEvent.EventType</c>). One event type today; more (cash movements, fees) are future work
    /// per <c>real-time-risk/README.md</c>'s build order.
    /// </summary>
    public const string TradeBookedEventType = "TradeBooked";
}
