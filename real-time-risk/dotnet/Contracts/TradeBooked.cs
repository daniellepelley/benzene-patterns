namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>
/// The immutable event appended to the ledger's <c>IEventStore</c> for every booked trade (JSON body of
/// the <see cref="Topics.TradeBookedEventType"/> <c>EventEnvelope</c>) - the Trade Ledger's write side
/// and, via the event table's DynamoDB Stream, the Risk Read Models' projection input.
/// </summary>
public class TradeBooked
{
    public Guid TradeId { get; set; }
    public string Book { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
    public DateTimeOffset BookedAt { get; set; }
}
