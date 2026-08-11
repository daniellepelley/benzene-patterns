using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Serves "which books are exposed to this symbol?" from the same in-memory projection
/// <see cref="BookPositionsQueryHandler"/> reads - a second read path over existing state, not a
/// second projection. The Valuation Service calls this every time a <see cref="BarClosed"/> arrives,
/// to find the positions a new mark applies to.
/// </summary>
[Message(Topics.SymbolPositions)]
[HttpEndpoint("GET", "/positions/by-symbol/{symbol}")]
public class SymbolPositionsQueryHandler : IMessageHandler<SymbolPositionsRequest, SymbolPositionsResponse>
{
    private readonly BookPositionsStore _store;

    public SymbolPositionsQueryHandler(BookPositionsStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<SymbolPositionsResponse>> HandleAsync(SymbolPositionsRequest message)
    {
        if (string.IsNullOrWhiteSpace(message.Symbol))
        {
            return Task.FromResult(BenzeneResult.ValidationError<SymbolPositionsResponse>("Symbol is required."));
        }

        return Task.FromResult(BenzeneResult.Ok(_store.QueryBySymbol(message.Symbol)));
    }
}
