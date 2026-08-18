using System.Collections.Concurrent;
using Benzene.Patterns.Cqrs.Contracts;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// The denormalized view: a tenant and its users, together, in one row.
/// </summary>
/// <remarks>
/// <para>
/// Illegal on the write side and correct here. It holds data from two aggregates that no core
/// service may hold together, and it earns that by being <b>derived</b>: nothing writes to it except
/// the projection, nothing treats it as authoritative, and it can be thrown away and rebuilt from
/// the events that produced it.
/// </para>
/// <para>
/// <b>Every mutation here is an idempotent fold.</b> <c>UpsertTenant</c> and <c>AddUser</c>, never
/// <c>IncrementUserCount</c>. That is not style: delivery is at-least-once and a rebuild replays
/// everything, so an operation that is not a fold drifts a little further from the truth on every
/// redelivery, silently, and a rebuild produces a different answer from the live view.
/// </para>
/// </remarks>
public class ReadStore
{
    private sealed class Row
    {
        public string Company = string.Empty;
        public int Version;
        public readonly ConcurrentDictionary<string, string> Users = new();
    }

    private readonly ConcurrentDictionary<string, Row> _tenants = new();

    /// <summary>Applies a tenant create or rename. Last writer wins BY VERSION, not by arrival.</summary>
    /// <remarks>
    /// Nothing promises ordering: two publishers, one exchange, retries and requeues in between. A
    /// rename that overtakes its own create, or a replay that re-delivers the create after the
    /// rename, would silently revert the company name if the fold trusted arrival order. Comparing
    /// versions makes the fold order-insensitive, which is what makes replay safe.
    /// </remarks>
    public void UpsertTenant(string tenantId, string company, int version)
    {
        _tenants.AddOrUpdate(tenantId,
            _ => new Row { Company = company, Version = version },
            (_, existing) =>
            {
                if (version >= existing.Version)
                {
                    existing.Company = company;
                    existing.Version = version;
                }

                return existing;
            });
    }

    /// <summary>Adds a user to its tenant's row, creating a stub if the tenant event has not arrived.</summary>
    /// <remarks>
    /// The stub is the honest answer to out-of-order arrival, not a workaround. Two independent
    /// services publish to the same exchange with no ordering between them, so a user event genuinely
    /// can arrive first. The row then knows a tenant id and nothing else, and reports
    /// <c>tenantVersion: 0</c> so a reader can tell "not caught up yet" from "this tenant has no
    /// name". When the tenant event lands, the fold fills it in.
    /// </remarks>
    public void AddUser(string tenantId, string userId, string email)
    {
        var row = _tenants.GetOrAdd(tenantId, _ => new Row());
        row.Users[userId] = email;
    }

    public TenantUsersView? Get(string tenantId)
    {
        if (!_tenants.TryGetValue(tenantId, out var row))
        {
            return null;
        }

        return Project(tenantId, row);
    }

    public List<TenantUsersView> All()
        => _tenants
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => Project(x.Key, x.Value))
            .ToList();

    /// <summary>Throws the whole view away. Safe precisely because it is derived.</summary>
    public void Clear() => _tenants.Clear();

    private static TenantUsersView Project(string tenantId, Row row)
    {
        var users = row.Users
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new UserView { UserId = x.Key, Email = x.Value })
            .ToList();

        return new TenantUsersView
        {
            TenantId = tenantId,
            Company = row.Company,
            TenantVersion = row.Version,
            UserCount = users.Count,
            Users = users
        };
    }
}
