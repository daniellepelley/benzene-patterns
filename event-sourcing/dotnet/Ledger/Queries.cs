using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.EventSourcing;
using Benzene.Http;
using Benzene.Results;

namespace Benzene.Patterns.EventSourcing.Ledger;

public class AccountView
{
    public string AccountId { get; set; } = string.Empty;
    public string Holder { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public long BalancePence { get; set; }
    public long Version { get; set; }

    /// <summary>How many events this answer was folded from, and whether a snapshot was used.</summary>
    /// <remarks>
    /// Reported on the response rather than logged. A snapshot changes only this number — never the
    /// balance — and putting the number where a caller can see it is what turns "snapshots work" from
    /// a claim into something a test can fail on.
    /// </remarks>
    public int EventsRead { get; set; }

    public bool FromSnapshot { get; set; }
    public long SnapshotVersion { get; set; }
}

/// <summary>The current state: a fold of the log, computed on the way past.</summary>
[Message("account:get")]
[HttpEndpoint("GET", "/accounts/{accountId}")]
public class GetAccountHandler : IMessageHandler<GetAccountRequest, AccountView>
{
    private readonly Rehydrator _rehydrator;

    public GetAccountHandler(Rehydrator rehydrator)
    {
        _rehydrator = rehydrator;
    }

    public async Task<IBenzeneResult<AccountView>> HandleAsync(GetAccountRequest request)
    {
        var rehydrated = request.AsOf > 0
            ? await _rehydrator.AsOfAsync(request.AccountId, request.AsOf)
            : await _rehydrator.CurrentAsync(request.AccountId);

        return rehydrated.State.Exists
            ? BenzeneResult.Ok(View(request.AccountId, rehydrated))
            : BenzeneResult.NotFound<AccountView>();
    }

    internal static AccountView View(string accountId, Rehydrated r) => new()
    {
        AccountId = accountId,
        Holder = r.State.Holder,
        Currency = r.State.Currency,
        BalancePence = r.State.BalancePence,
        Version = r.State.Version,
        EventsRead = r.EventsRead,
        FromSnapshot = r.FromSnapshot,
        SnapshotVersion = r.SnapshotVersion
    };
}

public class GetAccountRequest
{
    public string AccountId { get; set; } = string.Empty;

    /// <summary>
    /// Fold only up to this stream version — the account as it stood then.
    /// </summary>
    /// <remarks>
    /// Not a feature that had to be built. A log and a pure fold already are a point-in-time query;
    /// this parameter just stops the fold early.
    /// </remarks>
    public long AsOf { get; set; }
}

/// <summary>
/// The audit trail. Not a separate system, not a mirrored table — the same events the balance is
/// made of, which is why the two can never disagree.
/// </summary>
[Message("account:history")]
[HttpEndpoint("GET", "/accounts/{accountId}/history")]
public class GetHistoryHandler : IMessageHandler<GetAccountRequest, HistoryResponse>
{
    private readonly IEventStore _store;

    public GetHistoryHandler(IEventStore store)
    {
        _store = store;
    }

    public async Task<IBenzeneResult<HistoryResponse>> HandleAsync(GetAccountRequest request)
    {
        var events = await _store.ReadAsync(request.AccountId);
        if (events.Count == 0)
        {
            return BenzeneResult.NotFound<HistoryResponse>();
        }

        return BenzeneResult.Ok(new HistoryResponse
        {
            AccountId = request.AccountId,
            Count = events.Count,
            // The event type AS WRITTEN, not as upcast. An audit trail that showed the upcast shape
            // would be showing what today's code believes rather than what was recorded, which is the
            // one thing an audit trail must not do.
            Events = events.Select(x => new HistoryEntry
            {
                Version = x.Version,
                EventType = x.EventType,
                Payload = x.Payload,
                TimestampUtc = x.Timestamp.UtcDateTime
            }).ToList()
        });
    }
}

public class HistoryResponse
{
    public string AccountId { get; set; } = string.Empty;
    public int Count { get; set; }
    public List<HistoryEntry> Events { get; set; } = new();
}

public class HistoryEntry
{
    public long Version { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; }
}

/// <summary>Folds to the head and caches the result. Writes nothing to the log.</summary>
[Message("account:snapshot")]
[HttpEndpoint("POST", "/snapshots")]
public class TakeSnapshotHandler : IMessageHandler<GetAccountRequest, AccountView>
{
    private readonly Rehydrator _rehydrator;

    public TakeSnapshotHandler(Rehydrator rehydrator)
    {
        _rehydrator = rehydrator;
    }

    public async Task<IBenzeneResult<AccountView>> HandleAsync(GetAccountRequest request)
    {
        var rehydrated = await _rehydrator.SnapshotAsync(request.AccountId);
        return rehydrated.State.Exists
            ? BenzeneResult.Ok(GetAccountHandler.View(request.AccountId, rehydrated))
            : BenzeneResult.NotFound<AccountView>();
    }
}

/// <summary>
/// Deletes every snapshot. The endpoint that proves snapshots are a cache.
/// </summary>
/// <remarks>
/// After this, every answer the ledger gives is identical and every one costs more events to
/// produce. If either half of that were untrue, the snapshots would be a second source of truth
/// wearing a performance hat.
/// </remarks>
[Message("snapshots:clear")]
[HttpEndpoint("POST", "/snapshots/clear")]
public class ClearSnapshotsHandler : IMessageHandler<Benzene.Abstractions.Results.Void, SnapshotsCleared>
{
    private readonly SnapshotStore _snapshots;

    public ClearSnapshotsHandler(SnapshotStore snapshots)
    {
        _snapshots = snapshots;
    }

    public Task<IBenzeneResult<SnapshotsCleared>> HandleAsync(Benzene.Abstractions.Results.Void request)
    {
        var cleared = _snapshots.Count;
        _snapshots.Clear();
        return BenzeneResult.Ok(new SnapshotsCleared { Cleared = cleared }).AsTask();
    }
}

public class SnapshotsCleared
{
    public int Cleared { get; set; }
}
