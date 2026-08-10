namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>The <see cref="Topics.BookPositions"/> query request.</summary>
public class BookPositionsRequest
{
    public string Book { get; set; } = string.Empty;
}

/// <summary>The <see cref="Topics.BookPositions"/> query response - one row per symbol traded in the book.</summary>
public class BookPositionsResponse
{
    public string Book { get; set; } = string.Empty;
    public List<PositionView> Positions { get; set; } = new();

    /// <summary>
    /// The highest ledger event version this projection has consumed for the book, so a caller can
    /// tell whether a just-booked trade has been reflected yet (CDC projection is eventually
    /// consistent, not read-your-writes).
    /// </summary>
    public long ProjectedThroughVersion { get; set; }
}

/// <summary>One symbol's net position and realized cash flow within a book, as projected from the ledger.</summary>
public class PositionView
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>Net quantity: buys add, sells subtract.</summary>
    public decimal NetQuantity { get; set; }

    /// <summary>Net cash flow from trading this symbol: a sell adds proceeds, a buy subtracts cost.</summary>
    public decimal RealizedCash { get; set; }
}
