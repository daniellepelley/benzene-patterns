using System.Text.Json;
using Benzene.EventSourcing;

namespace Benzene.Patterns.EventSourcing.Ledger;

/// <summary>
/// An account's state — never stored, always computed.
/// </summary>
/// <remarks>
/// Nothing writes this to a database. It exists for as long as it takes to make one decision, and
/// then it is thrown away. If it were persisted it would be a second source of truth able to disagree
/// with the log, and the log would stop being the truth.
/// </remarks>
public record Account
{
    public bool Exists { get; init; }
    public string Holder { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public long BalancePence { get; init; }

    /// <summary>The stream version this state reflects — what an appender must claim it saw.</summary>
    public long Version { get; init; }

    public static readonly Account None = new();
}

/// <summary>
/// The fold. One pure function, and the most important twelve lines in the pattern.
/// </summary>
/// <remarks>
/// <para>
/// <c>(state, event) =&gt; state</c>, with no clock, no store, no IO. That is what makes it
/// deterministic: the same events in the same order always produce the same account, whether they are
/// read live, replayed from a snapshot, replayed from the start of time, or run in a unit test with a
/// hand-written list.
/// </para>
/// <para>
/// It is also the same fold a <a href="../../cqrs-read-models/README.md">projection</a> would use.
/// Two different folds over one log is how a balance and a statement come to disagree.
/// </para>
/// </remarks>
public static class AccountFold
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Account Apply(Account state, StoredEvent stored)
    {
        // Upcast first, so the fold below only ever knows about today's shapes. A fold that carries a
        // branch per historical schema accumulates one per year and is never safe to delete from.
        var (eventType, payload) = Upcaster.Upcast(stored.EventType, stored.Payload);

        return eventType switch
        {
            EventTypes.AccountOpened => Opened(state, payload, stored.Version),
            EventTypes.MoneyDeposited => Deposited(state, payload, stored.Version),
            EventTypes.MoneyWithdrawn => Withdrawn(state, payload, stored.Version),

            // An unknown event type advances the version and changes nothing else. A newer writer may
            // have appended something this build has no opinion about, and refusing to fold would
            // make an old reader unable to read a stream it is otherwise perfectly able to serve.
            _ => state with { Version = stored.Version }
        };
    }

    public static Account Replay(Account from, IEnumerable<StoredEvent> events)
        => events.Aggregate(from, Apply);

    private static Account Opened(Account state, string payload, long version)
    {
        var e = JsonSerializer.Deserialize<AccountOpened>(payload, Json)!;
        return new Account
        {
            Exists = true,
            Holder = e.Holder,
            Currency = e.Currency,
            BalancePence = 0,
            Version = version
        };
    }

    private static Account Deposited(Account state, string payload, long version)
    {
        var e = JsonSerializer.Deserialize<MoneyDeposited>(payload, Json)!;
        return state with { BalancePence = state.BalancePence + e.Pence, Version = version };
    }

    private static Account Withdrawn(Account state, string payload, long version)
    {
        var e = JsonSerializer.Deserialize<MoneyWithdrawn>(payload, Json)!;
        return state with { BalancePence = state.BalancePence - e.Pence, Version = version };
    }
}
