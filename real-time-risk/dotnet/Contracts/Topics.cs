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

    /// <summary>
    /// Query: which books hold a position in a symbol, across every book. Handled by the Risk Read
    /// Models service; asked by the Valuation Service every time a bar closes.
    /// </summary>
    public const string SymbolPositions = "positions:by-symbol";

    /// <summary>
    /// Query: the latest mark-to-market valuations for a symbol. Handled by the Valuation Service.
    /// </summary>
    public const string SymbolValuations = "valuations:by-symbol";

    /// <summary>
    /// Event: a one-minute OHLC bar closed for a symbol. Emitted by the Market-Data Aggregator,
    /// reacted to by the Valuation Service - which knows nothing about who emitted it
    /// (docs/patterns/choreography.md).
    /// </summary>
    /// <remarks>
    /// <para><strong>Why <c>bar-closed</c> and not <c>bar:closed</c>.</strong>
    /// docs/patterns/reference-real-time-risk.md writes this event as <c>bar:closed</c>, matching the
    /// colon-separated convention the rest of this repo's topics use. Kafka topic names may only
    /// contain <c>[a-zA-Z0-9._-]</c>, so a colon can't survive the wire here - and Benzene's Kafka
    /// binding routes each record to a handler by matching the <em>literal Kafka topic name</em>
    /// against <c>[Message(...)]</c> (there is no separate topic attribute on this transport; see
    /// benzene-dotnet's <c>docs/getting-started-kafka.md</c> §1.3). The Benzene topic and the Kafka
    /// topic are therefore necessarily the same string, and it has to be Kafka-legal.</para>
    /// <para>The <c>:</c>-carrying topics above are only ever routed over HTTP or DynamoDB Streams,
    /// where nothing constrains the character set, so they are unaffected.</para>
    /// </remarks>
    public const string BarClosed = "bar-closed";

    /// <summary>
    /// The Kafka topic the raw synthetic market-data feed is produced to and the Market-Data
    /// Aggregator's stream binding consumes. Not a Benzene topic: a tick is a raw stream record with
    /// no Benzene envelope and no handler routing (see <see cref="MarketTick"/>) - this constant
    /// exists only so the producer and the consumer can't drift on the topic string.
    /// </summary>
    public const string MarketDataTicksKafkaTopic = "market-data-ticks";

    /// <summary>
    /// The event type discriminator stored on every ledger event (<c>EventEnvelope.EventType</c> /
    /// <c>StoredEvent.EventType</c>). One event type today; more (cash movements, fees) are future work
    /// per <c>real-time-risk/README.md</c>'s build order.
    /// </summary>
    public const string TradeBookedEventType = "TradeBooked";
}
