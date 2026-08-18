namespace Benzene.Patterns.RealTimeRisk.PricingService;

/// <summary>
/// The simulated market-data source behind the pricing feed.
/// </summary>
/// <remarks>
/// <para>
/// In the reference platform this service is fed by the Market-Data Aggregator
/// (docs/patterns/reference-real-time-risk.md §3). That service is not built yet - it is blocked on a
/// transport decision recorded in real-time-risk/README.md's roadmap - so prices here are simulated.
/// Simulated, and <b>said to be</b>: a demo that quietly invents numbers while looking like a feed is
/// worse than one that says where its numbers come from.
/// </para>
/// <para>
/// The walk is <b>deterministic</b>, seeded from the symbol and the tick sequence rather than from a
/// clock or a shared <c>Random</c>. Two consequences, both wanted: the same symbol at the same
/// sequence prices identically on every run and in every replica, so a smoke test can assert on a
/// value; and there is no shared mutable state, so concurrent subscribers cost nothing to serve.
/// </para>
/// </remarks>
public static class PriceFeed
{
    /// <summary>The instruments this feed knows. An unknown symbol is a NotFound, not a made-up price.</summary>
    private static readonly IReadOnlyDictionary<string, double> ReferencePrices =
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["AAPL"] = 150.25,
            ["MSFT"] = 402.10,
            ["GOOG"] = 141.80,
            ["AMZN"] = 178.35,
            ["TSLA"] = 244.60
        };

    /// <summary>Annualised volatility used for both the walk and the greeks. One number, so they agree.</summary>
    private const double Volatility = 0.25;

    /// <summary>Half-spread as a fraction of mid - a fixed 2bp each side.</summary>
    private const double HalfSpread = 0.0002;

    /// <summary>Risk-free rate for the option maths. Flat, and flat on purpose: a curve is not the point here.</summary>
    private const double RiskFreeRate = 0.04;

    /// <summary>Tenor of the notional option the greeks describe, in years (30 calendar days).</summary>
    private const double TenorYears = 30.0 / 365.0;

    public static bool IsKnown(string symbol) => ReferencePrices.ContainsKey(symbol);

    public static IEnumerable<string> KnownSymbols => ReferencePrices.Keys;

    /// <summary>
    /// Prices one symbol at one point in its sequence. Pure: same inputs, same tick, always.
    /// </summary>
    public static PriceTick Quote(string symbol, long sequence, DateTimeOffset asOf)
    {
        var reference = ReferencePrices[symbol];
        var mid = reference * (1.0 + Drift(symbol, sequence));

        return new PriceTick
        {
            // The canonical casing from the dictionary, not whatever the caller typed: lookups are
            // case-insensitive but the tick a desk records should be the instrument's real ticker.
            Symbol = Canonical(symbol),
            Mid = Round(mid),
            Bid = Round(mid * (1.0 - HalfSpread)),
            Ask = Round(mid * (1.0 + HalfSpread)),
            Greeks = AtmCallGreeks(mid),
            AsOfUtc = asOf.ToUniversalTime().ToString("O"),
            Sequence = sequence
        };
    }

    private static string Canonical(string symbol) =>
        ReferencePrices.Keys.First(k => string.Equals(k, symbol, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// A bounded, deterministic wobble in ±1%, derived by hashing (symbol, sequence).
    /// </summary>
    /// <remarks>
    /// Not a random walk accumulating from the last tick, which would need shared state and would
    /// drift arbitrarily far from the reference price over a long-running demo. Hashing the sequence
    /// keeps every tick independently reproducible and keeps the price recognisable as the symbol's.
    /// </remarks>
    private static double Drift(string symbol, long sequence)
    {
        var hash = 17L;
        foreach (var c in symbol.ToUpperInvariant())
        {
            hash = unchecked(hash * 31 + c);
        }

        hash = unchecked(hash * 31 + sequence);
        // Fold to a stable 0..9999 bucket, then to ±0.01.
        var bucket = (int)(((hash ^ (hash >> 33)) & 0x7FFFFFFF) % 10_000);
        return (bucket - 5_000) / 500_000.0;
    }

    /// <summary>
    /// Black-Scholes greeks for a notional at-the-money 30-day call on the symbol.
    /// </summary>
    /// <remarks>
    /// Greeks for an <i>option</i>, not for the cash equity, because a share's own sensitivities are
    /// degenerate - delta 1, everything else 0 - and printing those would make the reference doc's
    /// "price/greeks feed" decorative rather than real. At the money and at a fixed tenor keeps the
    /// maths to a dozen lines while the numbers stay the textbook ones a desk would recognise.
    /// </remarks>
    private static Greeks AtmCallGreeks(double spot)
    {
        var strike = spot; // at the money, by construction
        var sqrtT = Math.Sqrt(TenorYears);
        var d1 = (Math.Log(spot / strike) + (RiskFreeRate + 0.5 * Volatility * Volatility) * TenorYears)
                 / (Volatility * sqrtT);
        var d2 = d1 - Volatility * sqrtT;

        var pdf = Math.Exp(-0.5 * d1 * d1) / Math.Sqrt(2.0 * Math.PI);
        var discount = Math.Exp(-RiskFreeRate * TenorYears);

        var theta = -(spot * pdf * Volatility) / (2.0 * sqrtT)
                    - RiskFreeRate * strike * discount * NormalCdf(d2);

        return new Greeks
        {
            Delta = Round(NormalCdf(d1)),
            Gamma = Round(pdf / (spot * Volatility * sqrtT)),
            // Per 1 percentage point of vol, and per calendar day - the units a trader reads them in,
            // rather than the per-unit-vol and per-year figures the formulae produce.
            Vega = Round(spot * pdf * sqrtT / 100.0),
            Theta = Round(theta / 365.0)
        };
    }

    /// <summary>
    /// Standard normal CDF via Abramowitz &amp; Stegun 7.1.26 (|error| &lt; 7.5e-8) - enough for a
    /// demo feed, and it keeps this file dependency-free.
    /// </summary>
    private static double NormalCdf(double x)
    {
        var sign = x < 0 ? -1 : 1;
        var z = Math.Abs(x) / Math.Sqrt(2.0);
        var t = 1.0 / (1.0 + 0.3275911 * z);
        var y = 1.0 - (((((1.061405429 * t - 1.453152027) * t) + 1.421413741) * t - 0.284496736) * t
                       + 0.254829592) * t * Math.Exp(-z * z);
        return 0.5 * (1.0 + sign * y);
    }

    /// <summary>Six decimal places: enough for greeks, and it stops float noise reaching the wire.</summary>
    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);
}
