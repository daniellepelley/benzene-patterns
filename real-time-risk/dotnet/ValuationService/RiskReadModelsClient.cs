using System.Net.Http.Json;
using System.Text.Json;
using Benzene.Patterns.RealTimeRisk.Contracts;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// Asks Risk Read Models "which books are exposed to this symbol?" over its
/// <c>GET /positions/by-symbol/{symbol}</c> endpoint.
/// </summary>
/// <remarks>
/// A plain typed <see cref="HttpClient"/> rather than a Benzene outbound route, deliberately: this is
/// a synchronous <em>query</em> against another service's public REST surface - the same URL the
/// README's <c>curl</c> hits - not a fire-and-forget event on a topic. Benzene's outbound routing
/// earns its keep when the destination transport is a decision (SNS today, EventBridge tomorrow) and
/// the caller shouldn't know; here the destination is "that service's HTTP endpoint" and always will
/// be. The choreographed half of this service - reacting to <c>bar-closed</c> - is the part that goes
/// through Benzene's messaging, and it does.
/// </remarks>
public class RiskReadModelsClient
{
    private static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerOptions.Web);

    private readonly HttpClient _httpClient;

    public RiskReadModelsClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>Returns every book holding a position in <paramref name="symbol"/>.</summary>
    public async Task<SymbolPositionsResponse> GetExposureAsync(string symbol, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.GetAsync($"positions/by-symbol/{Uri.EscapeDataString(symbol)}", cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<SymbolPositionsResponse>(ResponseJson, cancellationToken)
               ?? new SymbolPositionsResponse { Symbol = symbol };
    }
}
