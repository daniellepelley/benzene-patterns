using Benzene.Patterns.RealTimeRisk.PricingService;
using Grpc.Core;

namespace Benzene.Patterns.RealTimeRisk.RiskWorker;

/// <summary>
/// Prices positions against the Pricing Service's live feed - "revaluing its slice against the day's
/// curves", in docs/patterns/reference-real-time-risk.md §4's words.
/// </summary>
/// <remarks>
/// Over gRPC, using the client generated from the <b>same</b> <c>pricing.proto</c> the Pricing
/// Service serves (referenced from that project rather than copied - one definition, so the two
/// cannot drift). This is also the point where the platform stops being a set of services and starts
/// being a platform: the risk number is computed from the read model's positions and the pricing
/// feed's marks, both of which are other services' own outputs.
/// </remarks>
public class MarkToMarket
{
    private readonly Pricing.PricingClient _pricing;

    public MarkToMarket(Pricing.PricingClient pricing)
    {
        _pricing = pricing;
    }

    /// <summary>
    /// The symbol's mid, or null when the feed does not know it.
    /// </summary>
    /// <remarks>
    /// Null, never zero. An unpriceable position valued at zero is wrong in the one direction that
    /// matters on a risk report, and wrong invisibly - so the caller collects the symbol and the run
    /// says which positions are missing from its total.
    /// </remarks>
    public async Task<decimal?> TryGetMidAsync(string symbol, CancellationToken cancellationToken)
    {
        try
        {
            var tick = await _pricing
                .GetPriceAsync(new PriceRequest { Symbol = symbol }, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            return (decimal)tick.Mid;
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            // The handler returned a Benzene NotFound and Benzene mapped it onto gRPC. An instrument
            // the feed does not cover is an expected, reportable gap - not a transport failure, and
            // not something to retry.
            return null;
        }
    }
}
