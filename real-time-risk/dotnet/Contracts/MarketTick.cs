namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>
/// One raw market-data tick: a single trade print on the feed the Market-Data Aggregator rolls into
/// OHLC bars.
/// </summary>
/// <remarks>
/// Deliberately <strong>not</strong> a Benzene-routed message and so deliberately without a topic
/// constant. A tick arrives as a raw record on a Kafka stream (consumed via <c>UseKafkaStream</c>,
/// which hands the handler the whole batch rather than routing each record to a
/// <c>[Message(...)]</c> handler), and its producer is a market-data feed, not a Benzene service. It
/// is JSON on the Kafka record's value; the record's <em>key</em> is the symbol, so Kafka's own
/// partitioner keeps one symbol's ticks on one partition and therefore in order - the shard-per-
/// symbol property docs/patterns/streaming-processing.md's worked example assumes.
/// </remarks>
public class MarketTick
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The traded price.</summary>
    public decimal Price { get; set; }

    /// <summary>The traded size, in shares - the quantity that accumulates into the bar's volume.</summary>
    public decimal Size { get; set; }

    /// <summary>When the tick occurred, which is what decides the minute bucket it falls in.</summary>
    public DateTimeOffset Timestamp { get; set; }
}
