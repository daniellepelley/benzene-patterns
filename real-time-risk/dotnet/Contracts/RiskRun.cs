namespace Benzene.Patterns.RealTimeRisk.Contracts;

/// <summary>The <see cref="Topics.RiskRun"/> command request - start an end-of-day risk run.</summary>
public class RiskRunRequest
{
    /// <summary>
    /// The books to cover. Named by the caller rather than discovered: the read model answers
    /// "positions for this book", not "every book that exists", and inventing a discovery endpoint to
    /// make the demo tidier would be inventing a contract the reference platform does not have.
    /// </summary>
    public List<string> Books { get; set; } = new();

    /// <summary>Books per shard. Defaults to 1 - one worker per book - when unset or non-positive.</summary>
    public int ShardSize { get; set; }
}

/// <summary>The <see cref="Topics.RiskRun"/> response - the firm-level number and its coverage.</summary>
public class RiskRunResponse
{
    public Guid RunId { get; set; }

    /// <summary>How many shards the books were partitioned into, i.e. how wide the scatter went.</summary>
    public int ShardCount { get; set; }

    /// <summary>Mark-to-market value of every position that could be priced.</summary>
    public decimal MarketValue { get; set; }

    /// <summary>Realized cash across the covered books, as the read model projected it.</summary>
    public decimal RealizedCash { get; set; }

    /// <summary>The end-of-day number: mark-to-market plus realized cash.</summary>
    public decimal TotalValue { get; set; }

    public int PositionsValued { get; set; }

    /// <summary>
    /// Symbols the price feed did not recognise, so their positions are NOT in
    /// <see cref="MarketValue"/>.
    /// </summary>
    /// <remarks>
    /// The reason this is a list and not a count. A risk number that quietly treats an unpriceable
    /// position as worth zero is wrong in the one direction that matters, and it is wrong invisibly -
    /// so the symbols are named and <see cref="IsFullyPriced"/> says whether the number is complete.
    /// </remarks>
    public List<string> UnpricedSymbols { get; set; } = new();

    /// <summary>Shards whose worker failed or returned an unsuccessful result. See <see cref="IsComplete"/>.</summary>
    public List<string> FailedShards { get; set; } = new();

    /// <summary>
    /// False when any shard failed - the total covers only the shards that came back.
    /// </summary>
    /// <remarks>
    /// Straight from <c>ScatterGatherResult.IsComplete</c>. The whole point of
    /// <c>Benzene.MapReduce</c>'s explicit partial-failure policy is that an incomplete total is never
    /// mistaken for a complete one, and this is where that reaches the caller.
    /// </remarks>
    public bool IsComplete { get; set; }

    /// <summary>False when at least one position could not be priced. Distinct from <see cref="IsComplete"/>.</summary>
    /// <remarks>
    /// Two different kinds of incompleteness, kept apart on purpose: a failed SHARD means a slice of
    /// the book was never valued at all, while an unpriced SYMBOL means it was valued and the price
    /// was missing. They have different causes and different fixes, and collapsing them into one
    /// "complete" flag would tell an operator to go looking in the wrong place.
    /// </remarks>
    public bool IsFullyPriced { get; set; }
}

/// <summary>The <see cref="Topics.RiskShard"/> request - one slice of the book, for one worker.</summary>
public class RiskShardRequest
{
    public Guid RunId { get; set; }
    public string ShardId { get; set; } = string.Empty;
    public List<string> Books { get; set; } = new();
}

/// <summary>The <see cref="Topics.RiskShard"/> response - one worker's partial risk vector.</summary>
public class RiskShardResponse
{
    public string ShardId { get; set; } = string.Empty;
    public decimal MarketValue { get; set; }
    public decimal RealizedCash { get; set; }
    public int PositionsValued { get; set; }
    public List<string> UnpricedSymbols { get; set; } = new();
}
