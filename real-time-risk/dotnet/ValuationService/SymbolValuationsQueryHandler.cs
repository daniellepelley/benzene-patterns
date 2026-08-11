using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// Serves the latest mark-to-market valuations for a symbol - the query that makes the whole
/// tick -&gt; bar -&gt; revaluation chain observable with a single <c>curl</c>, the way
/// <c>GET /books/{book}/positions</c> does for the ledger -&gt; read-model chain.
/// </summary>
[Message(Topics.SymbolValuations)]
[HttpEndpoint("GET", "/valuations/by-symbol/{symbol}")]
public class SymbolValuationsQueryHandler : IMessageHandler<SymbolValuationsRequest, SymbolValuationsResponse>
{
    private readonly PositionValuationStore _store;

    public SymbolValuationsQueryHandler(PositionValuationStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<SymbolValuationsResponse>> HandleAsync(SymbolValuationsRequest message)
    {
        if (string.IsNullOrWhiteSpace(message.Symbol))
        {
            return Task.FromResult(BenzeneResult.ValidationError<SymbolValuationsResponse>("Symbol is required."));
        }

        return Task.FromResult(BenzeneResult.Ok(_store.Query(message.Symbol)));
    }
}
