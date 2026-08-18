using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.PricingService.Handlers;

/// <summary>
/// Server-streaming subscription: one symbol in, a live tick stream out.
/// </summary>
/// <remarks>
/// <para>
/// The response type is <c>IAsyncEnumerable&lt;PriceTick&gt;</c> and that is the whole of it - Benzene
/// bridges the enumerable onto gRPC's <c>IServerStreamWriter</c>. No streaming API appears in this
/// file.
/// </para>
/// <para>
/// <b>Deadlines propagate end to end</b>, which the reference doc calls out for this service.
/// <see cref="IGrpcServerCallAccessor"/> exposes the call's <c>CancellationToken</c> - already
/// carrying the client's gRPC deadline as well as a disconnect - and the loop honours it. Without
/// that, a subscriber walking away leaves this generator producing ticks nobody is reading.
/// </para>
/// </remarks>
[GrpcMethod("/pricing.Pricing/SubscribePrices")]
[Message(Topics.PriceSubscribe)]
public class SubscribePricesHandler : IMessageHandler<PriceRequest, IAsyncEnumerable<PriceTick>>
{
    /// <summary>Gap between ticks. Fast enough to look live, slow enough to read in a terminal.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly IGrpcServerCallAccessor _call;

    public SubscribePricesHandler(IGrpcServerCallAccessor call)
    {
        _call = call;
    }

    public Task<IBenzeneResult<IAsyncEnumerable<PriceTick>>> HandleAsync(PriceRequest request)
    {
        if (!PriceFeed.IsKnown(request.Symbol))
        {
            return BenzeneResult.NotFound<IAsyncEnumerable<PriceTick>>().AsTask();
        }

        return BenzeneResult.Ok(Ticks(request, _call.CancellationToken)).AsTask();
    }

    private static async IAsyncEnumerable<PriceTick> Ticks(
        PriceRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (long sequence = 0; !cancellationToken.IsCancellationRequested; sequence++)
        {
            // MaxTicks is a convenience for callers that want a finite stream - a smoke test, or a
            // one-off capture - so they need not race a deadline to get a clean end. Zero means
            // "until the deadline or cancellation", which is the normal subscription.
            if (request.MaxTicks > 0 && sequence >= request.MaxTicks)
            {
                yield break;
            }

            yield return PriceFeed.Quote(request.Symbol, sequence, DateTimeOffset.UtcNow);

            // Delay before the NEXT tick, not after the last one: a bounded stream should end as soon
            // as it has said everything, rather than making the caller wait out one more interval.
            if (request.MaxTicks > 0 && sequence + 1 >= request.MaxTicks)
            {
                yield break;
            }

            await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);
        }
    }
}
