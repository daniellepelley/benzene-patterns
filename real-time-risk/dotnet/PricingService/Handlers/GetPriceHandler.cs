using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.PricingService.Handlers;

/// <summary>
/// Unary snapshot: one price for one symbol, right now.
/// </summary>
/// <remarks>
/// An ordinary <see cref="IMessageHandler{TRequest,TResponse}"/> declaring the generated protobuf
/// types directly, so nothing is serialised through JSON on the way in or out. The only thing marking
/// it as gRPC at all is <see cref="GrpcMethodAttribute"/> - it is still topic-routed
/// (<see cref="Topics.PriceGet"/>) and still returns an <c>IBenzeneResult</c>, which is the reference
/// doc's point: "a Benzene service that merely speaks a faster wire to its neighbours".
/// </remarks>
[GrpcMethod("/pricing.Pricing/GetPrice")]
[Message(Topics.PriceGet)]
public class GetPriceHandler : IMessageHandler<PriceRequest, PriceTick>
{
    public Task<IBenzeneResult<PriceTick>> HandleAsync(PriceRequest request)
    {
        // NotFound rather than an invented price. Benzene maps the result status onto gRPC's
        // StatusCode.NotFound and a benzene-status trailer, so the caller gets a real gRPC error
        // without this handler knowing anything about gRPC.
        if (!PriceFeed.IsKnown(request.Symbol))
        {
            return BenzeneResult.NotFound<PriceTick>().AsTask();
        }

        return BenzeneResult.Ok(PriceFeed.Quote(request.Symbol, sequence: 0, DateTimeOffset.UtcNow)).AsTask();
    }
}
