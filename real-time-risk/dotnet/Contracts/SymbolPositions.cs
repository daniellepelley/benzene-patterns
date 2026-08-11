namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>The <see cref="Topics.SymbolPositions"/> query request.</summary>
public class SymbolPositionsRequest
{
    public string Symbol { get; set; } = string.Empty;
}

/// <summary>
/// The <see cref="Topics.SymbolPositions"/> query response - every book holding a position in the
/// symbol. The inverse of <see cref="BookPositionsResponse"/>'s "one book, every symbol", and the
/// read the Valuation Service needs to answer "who is exposed to AAPL?" when a bar closes.
/// </summary>
public class SymbolPositionsResponse
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>One row per book that has ever traded the symbol, ordered by book.</summary>
    public List<BookPositionView> Books { get; set; } = new();
}

/// <summary>One book's position in a single symbol.</summary>
public class BookPositionView
{
    public string Book { get; set; } = string.Empty;

    /// <summary>Net quantity: buys add, sells subtract.</summary>
    public decimal NetQuantity { get; set; }

    /// <summary>Net cash flow from trading this symbol in this book: a sell adds proceeds, a buy subtracts cost.</summary>
    public decimal RealizedCash { get; set; }
}
