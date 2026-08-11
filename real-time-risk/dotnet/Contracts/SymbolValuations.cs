namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>The <see cref="Topics.SymbolValuations"/> query request.</summary>
public class SymbolValuationsRequest
{
    public string Symbol { get; set; } = string.Empty;
}

/// <summary>
/// The <see cref="Topics.SymbolValuations"/> query response - what every exposed book's position in
/// the symbol was worth at the last mark the Valuation Service saw.
/// </summary>
public class SymbolValuationsResponse
{
    public string Symbol { get; set; } = string.Empty;

    /// <summary>The <see cref="BarClosed.Close"/> the valuations below were struck against.</summary>
    public decimal MarkPrice { get; set; }

    /// <summary>The end of the bar window that produced <see cref="MarkPrice"/>.</summary>
    public DateTimeOffset MarkedAt { get; set; }

    /// <summary>One row per book exposed to the symbol, ordered by book.</summary>
    public List<PositionValuation> Valuations { get; set; } = new();
}

/// <summary>
/// One book's position in one symbol, marked to the latest closing bar.
/// </summary>
/// <remarks>
/// <para><strong>What this deliberately does not claim.</strong> There is no unrealized-P&amp;L figure
/// here, because the data to compute one honestly does not exist yet. <see cref="PositionView"/> -
/// the Risk Read Models projection this is derived from - tracks <c>NetQuantity</c> and
/// <c>RealizedCash</c> but <em>no cost basis for the still-open position</em>: a true unrealized P&amp;L
/// needs the weighted-average entry price of the open lots, which nothing in the ledger projection
/// carries today. Rather than invent a number the inputs don't support, this type exposes the two
/// things that are genuinely known: the position's <see cref="MarketValue"/> (a mark-to-market
/// notional, <c>NetQuantity x Close</c>) and the <see cref="RealizedCash"/> already banked.</para>
/// <para><see cref="TotalValue"/> (<c>MarketValue + RealizedCash</c>) is the closest defensible
/// aggregate: for a book whose position started flat it <em>is</em> the total P&amp;L, but that is a
/// property of the book's history, not something this service can assert - so it is named for what
/// it computes, not for what it would mean under an assumption nobody has checked.</para>
/// <para>Adding weighted-average cost-basis tracking to the ledger projection - and only then a real
/// unrealized-P&amp;L field - is a clear follow-up, recorded in real-time-risk/README.md's roadmap.</para>
/// </remarks>
public class PositionValuation
{
    public string Book { get; set; } = string.Empty;

    /// <summary>Net quantity held, as projected from the ledger: buys add, sells subtract.</summary>
    public decimal NetQuantity { get; set; }

    /// <summary>
    /// Mark-to-market notional: <see cref="NetQuantity"/> x the bar's close. Negative for a short
    /// position. Not a P&amp;L number - see the remarks on this type.
    /// </summary>
    public decimal MarketValue { get; set; }

    /// <summary>The realized cash flow the ledger projection has already banked for this book/symbol.</summary>
    public decimal RealizedCash { get; set; }

    /// <summary><see cref="MarketValue"/> + <see cref="RealizedCash"/> - see the remarks on this type.</summary>
    public decimal TotalValue { get; set; }
}
