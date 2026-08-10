namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>The <see cref="Topics.BookTrade"/> command request.</summary>
public class BookTradeRequest
{
    public string Book { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public TradeSide Side { get; set; }
    public decimal Quantity { get; set; }
    public decimal Price { get; set; }
}

/// <summary>The <see cref="Topics.BookTrade"/> command response - the resulting ledger position.</summary>
public class BookTradeResponse
{
    public Guid TradeId { get; set; }
    public string Book { get; set; } = string.Empty;

    /// <summary>The book's ledger stream version after this trade was appended.</summary>
    public long Version { get; set; }
}
