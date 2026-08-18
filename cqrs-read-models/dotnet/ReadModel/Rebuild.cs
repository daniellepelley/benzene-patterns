using System.Net.Http.Json;
using System.Text.Json;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.Cqrs.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// Throws the view away and rebuilds it by replaying the write side's events.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the operation that proves a read model is a read model.</b> A store you cannot delete
/// and reconstruct is not derived — it is a second source of truth that nobody labelled as one, and
/// every bug in it is now a data-loss incident rather than a redeploy. Rebuild is what makes fixing
/// a projection bug, adding a field, or standing up a new view routine.
/// </para>
/// <para>
/// It replays from the write services' own event logs, because a fanout exchange keeps nothing: it
/// delivers to the queues bound at publish time and forgets. The replay source has to live next to
/// the data that produced it — the <a href="../../transactional-outbox/README.md">outbox table</a>,
/// or a durable log.
/// </para>
/// <para>
/// A production rebuild projects into a NEW store and swaps at the end, so reads keep working and a
/// failed rebuild leaves the old view intact. This one clears in place, which is simpler to read and
/// briefly serves an empty view — an honest simplification, not a recommendation.
/// </para>
/// </remarks>
[Message("readmodel:rebuild")]
[HttpEndpoint("POST", "/rebuild")]
public class RebuildHandler : IMessageHandler<RebuildRequest, RebuildResponse>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ReadStore _view;
    private readonly ReplaySources _sources;
    private readonly HttpClient _http;
    private readonly ILogger<RebuildHandler> _logger;

    public RebuildHandler(ReadStore view, ReplaySources sources, HttpClient http, ILogger<RebuildHandler> logger)
    {
        _view = view;
        _sources = sources;
        _http = http;
        _logger = logger;
    }

    public async Task<IBenzeneResult<RebuildResponse>> HandleAsync(RebuildRequest request)
    {
        var recorded = new List<RecordedEvent>();
        foreach (var url in _sources.EventLogUrls)
        {
            var log = await _http.GetFromJsonAsync<EventLogResponse>(url, Json);
            if (log == null)
            {
                return BenzeneResult.ServiceUnavailable<RebuildResponse>($"No event log at {url}.");
            }

            recorded.AddRange(log.Events);
        }

        _view.Clear();

        // Deliberately NOT sorted into a global order. Each service's log is ordered within itself
        // and there is no clock that orders them against each other - which is exactly the condition
        // the live pipeline runs under. If the folds are order-insensitive, replaying in an arbitrary
        // interleaving reproduces the live view; if they are not, this is where that shows up.
        //
        // `reverse` exists to make that claim TESTABLE rather than asserted. Replaying the whole
        // history backwards - renames before their creates, users before their tenants - must produce
        // a byte-identical view. If it does not, the projection has an ordering dependency it did not
        // admit to, and the next redelivery or rebuild will quietly produce a different answer.
        var order = request.Reverse ? Enumerable.Reverse(recorded) : recorded;
        foreach (var e in order)
        {
            Apply(e);
        }

        _logger.LogInformation("Rebuilt the view from {Count} events (reverse: {Reverse})",
            recorded.Count, request.Reverse);

        return BenzeneResult.Ok(new RebuildResponse
        {
            EventsReplayed = recorded.Count,
            Reversed = request.Reverse
        });
    }

    private void Apply(RecordedEvent e)
    {
        switch (e.Topic)
        {
            case Topics.TenantCreated:
            {
                var payload = JsonSerializer.Deserialize<TenantCreated>(e.Body, Json)!;
                _view.UpsertTenant(payload.TenantId, payload.Company, payload.Version);
                break;
            }

            case Topics.TenantRenamed:
            {
                var payload = JsonSerializer.Deserialize<TenantRenamed>(e.Body, Json)!;
                _view.UpsertTenant(payload.TenantId, payload.Company, payload.Version);
                break;
            }

            case Topics.UserCreated:
            {
                var payload = JsonSerializer.Deserialize<UserCreated>(e.Body, Json)!;
                _view.AddUser(payload.TenantId, payload.UserId, payload.Email);
                break;
            }

            // An unrecognised topic is skipped rather than fatal: a replay source may hold events
            // this version of the projection has no opinion about, and refusing to rebuild because
            // of one would make the rebuild less available than the thing it repairs.
        }
    }
}

public class RebuildRequest
{
    /// <summary>Replay the history backwards. See the note in <see cref="RebuildHandler"/>.</summary>
    public bool Reverse { get; set; }
}

public class RebuildResponse
{
    public int EventsReplayed { get; set; }
    public bool Reversed { get; set; }
}

/// <summary>Where to replay from. Configuration, not a dependency the message path uses.</summary>
public class ReplaySources
{
    public ReplaySources(params string[] eventLogUrls)
    {
        EventLogUrls = eventLogUrls;
    }

    public IReadOnlyList<string> EventLogUrls { get; }
}
