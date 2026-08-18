using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.ModularMonolith.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.ModularMonolith.Modules.Billing;

/// <summary>
/// Charges a customer.
/// </summary>
/// <remarks>
/// Note what this handler does NOT know: whether the caller is in the same process. It has no
/// transport, no HTTP context, no queue. That is what lets the identical assembly serve both
/// deployments in this example - the monolith mounts it behind an in-process pipeline, the extracted
/// service behind an HTTP one, and neither the handler nor its tests change.
/// </remarks>
[Message(Topics.BillingCharge)]
public class ChargeCardHandler : IMessageHandler<ChargeCardRequest, ChargeCardResponse>
{
    private readonly BillingStore _store;

    public ChargeCardHandler(BillingStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<ChargeCardResponse>> HandleAsync(ChargeCardRequest request)
    {
        // Rule 4: domain failure crosses the boundary as a STATUS, never as an exception type the
        // caller would have to reference. The caller handles `not-found` and `validation-error` on
        // day one, in process, where they are cheap to write and easy to test.
        if (!_store.IsKnownCustomer(request.Customer))
        {
            return BenzeneResult.NotFound<ChargeCardResponse>().AsTask();
        }

        if (request.Amount <= 0)
        {
            return BenzeneResult.ValidationError<ChargeCardResponse>("Amount must be positive.").AsTask();
        }

        return BenzeneResult.Ok(new ChargeCardResponse
        {
            ChargeId = _store.Charge(request.Amount),
            Amount = request.Amount
        }).AsTask();
    }
}

/// <summary>
/// Reverses a charge - the compensating action when a later step of the order fails.
/// </summary>
/// <remarks>
/// Idempotent on purpose (rule 5): a second refund of the same charge reports <c>Refunded: false</c>
/// rather than refunding twice. In process a send is exactly-once and this costs nothing; on a queue
/// it is at-least-once and this becomes load-bearing. Writing it now is the cheap insurance the
/// pattern recommends - retrofitting idempotency under production duplicate traffic is not cheap.
/// </remarks>
[Message(Topics.BillingRefund)]
public class RefundChargeHandler : IMessageHandler<RefundChargeRequest, RefundChargeResponse>
{
    private readonly BillingStore _store;

    public RefundChargeHandler(BillingStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<RefundChargeResponse>> HandleAsync(RefundChargeRequest request)
    {
        return BenzeneResult.Ok(new RefundChargeResponse
        {
            ChargeId = request.ChargeId,
            Refunded = _store.Refund(request.ChargeId)
        }).AsTask();
    }
}
