using System.Collections.Concurrent;
using Benzene.EventSourcing;

namespace Benzene.Patterns.EventSourcing.Ledger;

/// <summary>A folded state remembered at a version, so a long history need not be re-read.</summary>
/// <remarks>
/// A snapshot is a CACHE, never a record. Delete every snapshot in this store and the ledger answers
/// exactly the same questions, a little more slowly. That is the test of whether a snapshot
/// implementation is correct, and it is the assertion the smoke test makes.
/// </remarks>
public class SnapshotStore
{
    private readonly ConcurrentDictionary<string, Account> _snapshots = new();

    public void Save(string accountId, Account state) => _snapshots[accountId] = state;

    public Account? Get(string accountId) => _snapshots.TryGetValue(accountId, out var s) ? s : null;

    public void Clear() => _snapshots.Clear();

    public int Count => _snapshots.Count;
}

/// <summary>The state a rehydration produced, and what it cost to produce it.</summary>
/// <remarks>
/// <see cref="EventsRead"/> is reported rather than kept private because it is the only way to tell a
/// snapshot that is working from one that is silently being ignored. Both give the right answer; only
/// one of them saved anything, and a snapshot that never gets used is a bug that never fails a test
/// unless the cost is visible.
/// </remarks>
public record Rehydrated(Account State, int EventsRead, bool FromSnapshot, long SnapshotVersion);

/// <summary>
/// Reads a stream and folds it. This is the piece Benzene deliberately does not ship.
/// </summary>
/// <remarks>
/// <para>
/// <c>Benzene.EventSourcing</c> gives an append-only store with optimistic concurrency and stops
/// there: no aggregate base class, no snapshot type, no replay driver. That is a considered line
/// rather than an omission — rehydration conventions vary enough between domains that a framework
/// abstraction usually gets in the way — and this file is what the other side of the line looks like.
/// It is about sixty lines.
/// </para>
/// </remarks>
public class Rehydrator
{
    private readonly IEventStore _store;
    private readonly SnapshotStore _snapshots;

    public Rehydrator(IEventStore store, SnapshotStore snapshots)
    {
        _store = store;
        _snapshots = snapshots;
    }

    /// <summary>Rehydrates to the head of the stream, using the latest snapshot if there is one.</summary>
    public async Task<Rehydrated> CurrentAsync(string accountId)
    {
        var snapshot = _snapshots.Get(accountId);
        var from = snapshot?.Version ?? 0;

        // Read FORWARD from the snapshot's version. This is the whole benefit: an account with a
        // decade of history and a recent snapshot reads the tail, not the decade.
        var tail = await _store.ReadAsync(accountId, from);
        var state = AccountFold.Replay(snapshot ?? Account.None, tail);

        return new Rehydrated(state, tail.Count, snapshot != null, from);
    }

    /// <summary>
    /// Rehydrates the account as it stood at <paramref name="asOfVersion"/> — the query event
    /// sourcing is bought for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "What did this account look like last Tuesday?" is not a feature that had to be designed; it
    /// is what a log plus a pure fold already is. No audit table, no history columns, no
    /// soft-delete flags — and no possibility of the audit trail disagreeing with the balance,
    /// because they are the same data.
    /// </para>
    /// <para>
    /// Snapshots are deliberately NOT used here. A snapshot is only valid at its own version, and one
    /// taken after the requested point would answer a different question. Reading from the start is
    /// the correct thing to do, and it is why this path reports a higher event count.
    /// </para>
    /// </remarks>
    public async Task<Rehydrated> AsOfAsync(string accountId, long asOfVersion)
    {
        var all = await _store.ReadAsync(accountId);
        var upTo = all.Where(x => x.Version <= asOfVersion).ToList();
        var state = AccountFold.Replay(Account.None, upTo);

        return new Rehydrated(state, upTo.Count, false, 0);
    }

    /// <summary>Folds to the head and remembers the result. Nothing is written to the log.</summary>
    public async Task<Rehydrated> SnapshotAsync(string accountId)
    {
        var rehydrated = await CurrentAsync(accountId);
        if (rehydrated.State.Exists)
        {
            _snapshots.Save(accountId, rehydrated.State);
        }

        return rehydrated;
    }
}
