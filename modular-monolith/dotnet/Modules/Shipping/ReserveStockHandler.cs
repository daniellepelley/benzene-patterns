using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.ModularMonolith.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.ModularMonolith.Modules.Shipping;

/// <summary>Reserves stock for an order.</summary>
[Message(Topics.ShippingReserve)]
public class ReserveStockHandler : IMessageHandler<ReserveStockRequest, ReserveStockResponse>
{
    private readonly ShippingStore _store;

    public ReserveStockHandler(ShippingStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<ReserveStockResponse>> HandleAsync(ReserveStockRequest request)
    {
        if (!_store.IsKnownSku(request.Sku))
        {
            return BenzeneResult.NotFound<ReserveStockResponse>().AsTask();
        }

        var remaining = _store.TryReserve(request.Sku, request.Quantity);
        if (remaining == null)
        {
            // Not enough stock is a CONFLICT, not a validation error: the request was well formed
            // and would have succeeded a moment ago. The distinction matters to the caller, which
            // compensates on this and gives up on a validation error.
            return BenzeneResult.Conflict<ReserveStockResponse>().AsTask();
        }

        return BenzeneResult.Ok(new ReserveStockResponse
        {
            ReservationId = $"rsv-{Guid.NewGuid():N}"[..12],
            RemainingStock = remaining.Value
        }).AsTask();
    }
}
