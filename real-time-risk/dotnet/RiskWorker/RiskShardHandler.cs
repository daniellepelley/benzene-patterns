using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.RiskWorker;

/// <summary>
/// Revalues one shard of the book and returns a partial risk vector - the <b>map</b> half of
/// docs/patterns/reference-real-time-risk.md §4.
/// </summary>
/// <remarks>
/// <para>
/// Stateless by construction: everything it needs arrives in the message or is fetched per call, so
/// the pool scales by adding replicas and any worker can serve any shard. In production this is a
/// burst of Lambdas behind a <c>SendAsync</c>; here it is N containers behind the same
/// <c>SendAsync</c>. The handler cannot tell, which is the property being demonstrated.
/// </para>
/// <para>
/// Note what is <b>not</b> here: no partial-failure handling, no fan-out, no aggregation. A shard
/// that cannot be valued returns an unsuccessful result and the coordinator's scatter-gather policy
/// decides what that means for the firm number. A worker that swallowed its own failure to keep the
/// run tidy would be the exact defect <c>Benzene.MapReduce</c>'s explicit policy exists to prevent.
/// </para>
/// </remarks>
[Message(Topics.RiskShard)]
public class RiskShardHandler : IMessageHandler<RiskShardRequest, RiskShardResponse>
{
    private readonly PositionSource _positions;
    private readonly MarkToMarket _marks;

    public RiskShardHandler(PositionSource positions, MarkToMarket marks)
    {
        _positions = positions;
        _marks = marks;
    }

    public async Task<IBenzeneResult<RiskShardResponse>> HandleAsync(RiskShardRequest message)
    {
        var response = new RiskShardResponse { ShardId = message.ShardId };
        var unpriced = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var book in message.Books)
        {
            var positions = await _positions.TryGetPositionsAsync(book, CancellationToken.None);
            if (positions == null)
            {
                // The read model did not answer for this book, so this shard's slice of the total is
                // unknown. Failing the whole shard is right: a partial partial is not a number
                // anybody can reduce, and reporting it as one would under-count the firm silently.
                return BenzeneResult.ServiceUnavailable<RiskShardResponse>();
            }

            response.RealizedCash += positions.Positions.Sum(x => x.RealizedCash);

            foreach (var position in positions.Positions)
            {
                var mid = await _marks.TryGetMidAsync(position.Symbol, CancellationToken.None);
                if (mid == null)
                {
                    unpriced.Add(position.Symbol);
                    continue;
                }

                response.MarketValue += position.NetQuantity * mid.Value;
                response.PositionsValued++;
            }
        }

        response.UnpricedSymbols = unpriced.ToList();
        return BenzeneResult.Ok(response);
    }
}
