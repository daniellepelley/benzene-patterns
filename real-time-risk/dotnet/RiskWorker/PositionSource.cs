using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Benzene.Patterns.RealTimeRisk.Contracts;

namespace Benzene.Patterns.RealTimeRisk.RiskWorker;

/// <summary>
/// Reads a book's positions from the Risk Read Models service.
/// </summary>
/// <remarks>
/// The worker reads the <b>read model</b>, not the ledger, and that is the point of the CQRS split
/// in docs/patterns/reference-real-time-risk.md §3: revaluing a book needs the current net position
/// per symbol, which is a fold the read model has already done. Replaying the whole event stream in
/// every worker on every run would be the thing that pattern exists to avoid.
/// </remarks>
public class PositionSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public PositionSource(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>
    /// The book's positions, or null when the read model does not answer.
    /// </summary>
    /// <remarks>
    /// Null rather than an empty list. "No positions" and "I could not ask" are different facts, and
    /// folding the second into the first would let a shard whose read model was down contribute a
    /// confident zero to a firm-level number. The caller turns null into a failed shard, which the
    /// scatter-gather's partial-failure policy then reports.
    /// </remarks>
    public async Task<BookPositionsResponse?> TryGetPositionsAsync(string book, CancellationToken cancellationToken)
    {
        var response = await _http.GetAsync($"{_baseUrl}/books/{Uri.EscapeDataString(book)}/positions", cancellationToken)
            .ConfigureAwait(false);

        // A book nobody has traded is a legitimate empty position set, not a failure - the read model
        // answers 404 for it, and a run covering a quiet book should succeed with nothing to value.
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new BookPositionsResponse { Book = book };
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<BookPositionsResponse>(Json, cancellationToken)
            .ConfigureAwait(false);
    }
}
