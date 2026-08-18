using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.MapReduce;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.RiskCoordinator;

/// <summary>
/// The end-of-day risk run from docs/patterns/reference-real-time-risk.md §4: partition the book into
/// shards, scatter <c>risk:shard</c> across the worker pool, fold the partials into the firm-level
/// number.
/// </summary>
/// <remarks>
/// <para>
/// The whole map-reduce is one call. <c>Benzene.MapReduce</c>'s <c>ScatterGatherAsync</c> is the
/// bounded fan-out over <c>SendAsync</c> plus an app-owned fold, packaged so an app does not
/// hand-roll it — and because the scatter goes through the <b>routing table</b>, this handler has no
/// idea whether a shard becomes a Lambda invoke or an HTTP call to a container. Only
/// <c>StartUp.cs</c> knows, in one line.
/// </para>
/// <para>
/// <b>BestEffort, deliberately.</b> The default is <c>ThrowOnAnyFailure</c>, which is the right
/// default for a regulatory total that is meaningless unless complete. This demo chooses the other
/// arm because the interesting property to show is the one the pattern doc calls out — that a
/// reduced-coverage answer <i>says so</i> — and a thrown exception shows nothing about coverage. The
/// response carries the failed shards and <c>IsComplete</c>, so an incomplete number is never
/// mistaken for a complete one either way.
/// </para>
/// </remarks>
[Message(Topics.RiskRun)]
[HttpEndpoint("POST", "/risk/runs")]
public class RiskRunHandler : IMessageHandler<RiskRunRequest, RiskRunResponse>
{
    /// <summary>
    /// Cap on simultaneous worker calls. The reference platform's cap exists to avoid opening
    /// hundreds of concurrent Lambda invocations at once; here it also keeps a laptop's Compose stack
    /// from being swamped by a run over a wide book.
    /// </summary>
    private const int MaxConcurrentShards = 8;

    private readonly IBenzeneMessageSender _sender;

    public RiskRunHandler(IBenzeneMessageSender sender)
    {
        _sender = sender;
    }

    public async Task<IBenzeneResult<RiskRunResponse>> HandleAsync(RiskRunRequest message)
    {
        var books = (message.Books ?? new List<string>())
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => b.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (books.Count == 0)
        {
            // Not an empty run returning zero. A firm-level number computed over no books is a
            // number nobody asked for, and returning 0.00 for it would be indistinguishable from a
            // genuinely flat firm.
            return BenzeneResult.ValidationError<RiskRunResponse>("At least one book is required.");
        }

        var runId = Guid.NewGuid();
        var shards = Partition(runId, books, message.ShardSize).ToList();

        var scattered = await _sender.ScatterGatherAsync<RiskShardRequest, RiskShardResponse, Accumulator>(
            Topics.RiskShard,
            shards,
            new Accumulator(),
            (accumulator, partial) => accumulator.Fold(partial),
            new ScatterGatherOptions
            {
                MaxDegreeOfParallelism = MaxConcurrentShards,
                PartialFailureMode = PartialFailureMode.BestEffort
            });

        var total = scattered.Value;
        return BenzeneResult.Ok(new RiskRunResponse
        {
            RunId = runId,
            ShardCount = shards.Count,
            MarketValue = total.MarketValue,
            RealizedCash = total.RealizedCash,
            TotalValue = total.MarketValue + total.RealizedCash,
            PositionsValued = total.PositionsValued,
            UnpricedSymbols = total.UnpricedSymbols.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            FailedShards = scattered.FailedShards.Select(x => x.ShardId).ToList(),
            IsComplete = scattered.IsComplete,
            IsFullyPriced = total.UnpricedSymbols.Count == 0
        });
    }

    /// <summary>
    /// Splits the books into shards of <paramref name="shardSize"/>.
    /// </summary>
    /// <remarks>
    /// Book-aligned rather than position-aligned, because a book is the unit the read model answers
    /// for — splitting one book across two workers would make both fetch the same projection and then
    /// need a rule for who counts what. Real platforms shard finer than this; the partitioning
    /// strategy is the app's to choose and is not what the pattern is about.
    /// </remarks>
    private static IEnumerable<RiskShardRequest> Partition(Guid runId, List<string> books, int shardSize)
    {
        var size = shardSize > 0 ? shardSize : 1;

        for (var i = 0; i < books.Count; i += size)
        {
            yield return new RiskShardRequest
            {
                RunId = runId,
                ShardId = $"shard-{i / size}",
                Books = books.Skip(i).Take(size).ToList()
            };
        }
    }

    /// <summary>
    /// The reduce accumulator - a deterministic fold over the partials.
    /// </summary>
    /// <remarks>
    /// Mutating and returning <c>this</c> rather than allocating per shard: the fold runs on one
    /// thread after every shard has completed (<c>BoundedFanOut</c> returns results in source order),
    /// so there is no concurrency here to guard against and no reason to copy.
    /// </remarks>
    private sealed class Accumulator
    {
        public decimal MarketValue { get; private set; }
        public decimal RealizedCash { get; private set; }
        public int PositionsValued { get; private set; }
        public HashSet<string> UnpricedSymbols { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Accumulator Fold(RiskShardResponse partial)
        {
            MarketValue += partial.MarketValue;
            RealizedCash += partial.RealizedCash;
            PositionsValued += partial.PositionsValued;
            foreach (var symbol in partial.UnpricedSymbols ?? new List<string>())
            {
                UnpricedSymbols.Add(symbol);
            }

            return this;
        }
    }
}
