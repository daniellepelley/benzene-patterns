using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.ModularMonolith.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.ModularMonolith.Modules.Orders;

/// <summary>
/// Places an order by calling the Billing and Shipping modules - <b>by topic, and by nothing else</b>.
/// </summary>
/// <remarks>
/// <para>
/// This file is the whole point of the pattern. Read it and try to work out whether Billing and
/// Shipping are in this process. You cannot, because nothing here says: the call site names a topic,
/// the sender resolves it through the routing table, and the routing table lives in the host's
/// StartUp. That is why extraction is a wiring change - this file is byte-identical in the monolith
/// deployment and in the three-service one.
/// </para>
/// <para>
/// <b>Failure is in the signature.</b> Every cross-module call comes back as a result with a status,
/// and this handler branches on those statuses today, in process, where <c>service-unavailable</c>
/// never actually happens. When the call later crosses a network and it does happen, the handling
/// code already exists - which is the part of distribution that is normally a rewrite.
/// </para>
/// <para>
/// The refund below is a compensation, not a transaction. In one process these two writes COULD have
/// shared a database transaction; the pattern forbids it (rule 2) precisely so that extraction does
/// not turn that convenience into a consistency bug. What remains is a two-step saga, which is the
/// <see href="https://github.com/daniellepelley/Benzene/blob/main/docs/patterns/orchestrators.md">orchestrators</see>
/// pattern in miniature.
/// </para>
/// </remarks>
[Message(Topics.OrderPlace)]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderHandler : IMessageHandler<PlaceOrderRequest, PlaceOrderResponse>
{
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<PlaceOrderHandler> _logger;
    private readonly string _deployment;

    public PlaceOrderHandler(IBenzeneMessageSender sender, ILogger<PlaceOrderHandler> logger, DeploymentName deployment)
    {
        _sender = sender;
        _logger = logger;
        // Reported back so a reader can see WHICH deployment answered. The handler does not discover
        // this - it is TOLD, by the host - because discovering it would mean the handler knew
        // something about its own transport, which is the one thing this example claims it does not.
        _deployment = deployment.Value;
    }

    public async Task<IBenzeneResult<PlaceOrderResponse>> HandleAsync(PlaceOrderRequest request)
    {
        if (request.Quantity <= 0)
        {
            return BenzeneResult.ValidationError<PlaceOrderResponse>("Quantity must be positive.");
        }

        var orderId = $"ord-{Guid.NewGuid():N}"[..12];
        var amount = request.Quantity * request.UnitPrice;

        // Nothing here says "billing is in this process".
        var charge = await _sender.SendAsync<ChargeCardRequest, ChargeCardResponse>(
            Topics.BillingCharge,
            new ChargeCardRequest { OrderId = orderId, Customer = request.Customer, Amount = amount });

        if (!charge.IsSuccessful)
        {
            // The status is passed through rather than flattened: an unknown customer (not-found)
            // and a bad amount (validation-error) are different answers to the caller, and the whole
            // reason failures cross as statuses is so that distinction survives the boundary.
            return BenzeneResult.Set<PlaceOrderResponse>(charge.Status, isSuccessful: false);
        }

        var reservation = await _sender.SendAsync<ReserveStockRequest, ReserveStockResponse>(
            Topics.ShippingReserve,
            new ReserveStockRequest { OrderId = orderId, Sku = request.Sku, Quantity = request.Quantity });

        if (!reservation.IsSuccessful)
        {
            // COMPENSATE. The money moved and the goods did not, and there is no shared transaction
            // to roll back - in this deployment because the pattern forbade one, in the extracted
            // deployment because there could not have been one anyway.
            _logger.LogWarning(
                "Order {OrderId}: stock reservation failed with {Status}; refunding charge {ChargeId}",
                orderId, reservation.Status, charge.Payload.ChargeId);

            var refund = await _sender.SendAsync<RefundChargeRequest, RefundChargeResponse>(
                Topics.BillingRefund, new RefundChargeRequest { ChargeId = charge.Payload.ChargeId });

            if (!refund.IsSuccessful)
            {
                // A failed compensation is the genuinely bad case - money taken, goods not shipped,
                // refund not applied. It is logged loudly rather than swallowed, because the honest
                // answer to the caller is still "your order failed" and somebody has to reconcile.
                _logger.LogError("Order {OrderId}: REFUND FAILED for charge {ChargeId} - needs reconciliation",
                    orderId, charge.Payload.ChargeId);
            }

            return BenzeneResult.Set<PlaceOrderResponse>(reservation.Status, isSuccessful: false);
        }

        return BenzeneResult.Ok(new PlaceOrderResponse
        {
            OrderId = orderId,
            ChargeId = charge.Payload.ChargeId,
            ReservationId = reservation.Payload.ReservationId,
            Amount = amount,
            RemainingStock = reservation.Payload.RemainingStock,
            Deployment = _deployment
        });
    }
}
