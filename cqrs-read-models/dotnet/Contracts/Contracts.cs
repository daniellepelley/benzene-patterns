namespace Benzene.Patterns.Cqrs.Contracts;

public static class WireHeaders
{
    /// <summary>
    /// The correlation-id header key, named explicitly at both ends rather than left to a default.
    /// See the same constant in the choreography example for why.
    /// </summary>
    public const string CorrelationId = "x-correlation-id";
}

/// <summary>
/// Two aggregates, three events, and one query that no single aggregate can answer.
/// </summary>
/// <remarks>
/// The split is the pattern. <c>tenant:*</c> and <c>user:*</c> are commands on ONE aggregate each —
/// the write model, share-nothing, exactly as <c>core-services.md</c> requires. The <c>*:created</c>
/// and <c>*:renamed</c> topics are the events they announce. And <c>tenant:users:list</c> is a query
/// spanning both, which is precisely the question the write side is forbidden to answer: the Tenant
/// service must not know users exist, so nothing on the write side may hold a tenant and its users
/// together.
/// </remarks>
public static class Topics
{
    // ── Write model: commands, one aggregate each ───────────────────────────────────────────────
    public const string TenantCreate = "tenant:create";
    public const string TenantRename = "tenant:rename";
    public const string UserCreate = "user:create";

    // ── Events: what happened, announced to nobody in particular ────────────────────────────────
    public const string TenantCreated = "tenant:created";
    public const string TenantRenamed = "tenant:renamed";
    public const string UserCreated = "user:created";

    // ── Read model: the query the write side cannot serve ───────────────────────────────────────
    public const string TenantUsersList = "tenant:users:list";
}

// ── Commands ────────────────────────────────────────────────────────────────────────────────────

public class CreateTenantRequest
{
    public string Company { get; set; } = string.Empty;

    /// <summary>
    /// Publish the resulting event this many times. The demo's stand-in for at-least-once delivery:
    /// a projection must be a fold that converges, not an increment that drifts.
    /// </summary>
    public int EmitTimes { get; set; } = 1;
}

public class RenameTenantRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
}

public class CreateUserRequest
{
    /// <summary>Reference by id. The User aggregate holds a tenant id and nothing else about tenants.</summary>
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int EmitTimes { get; set; } = 1;
}

// ── Events ──────────────────────────────────────────────────────────────────────────────────────

public class TenantCreated
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;

    /// <summary>
    /// The aggregate's version after this event — 1 for the first, 2 for the next, and so on.
    /// </summary>
    /// <remarks>
    /// Carried so the projection can fold LAST-WRITER-WINS by version rather than by arrival order.
    /// Nothing in a fanout promises ordering across publishers, and a rename that overtakes its own
    /// create would otherwise be silently undone by the replay of an older event.
    /// </remarks>
    public int Version { get; set; }
}

public class TenantRenamed
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public int Version { get; set; }
}

public class UserCreated
{
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

// ── Command responses ───────────────────────────────────────────────────────────────────────────

public class TenantAccepted
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public int Version { get; set; }
    public int Emitted { get; set; }
}

public class UserAccepted
{
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public int Emitted { get; set; }
}

// ── The read model's shape ──────────────────────────────────────────────────────────────────────

/// <summary>
/// A tenant and all its users, in one object. Illegal on the write side; the whole point here.
/// </summary>
/// <remarks>
/// This shape exists because somebody asked a question, not because the domain is shaped like this.
/// It is derived, disposable, and rebuildable — which is exactly what earns it the right to break
/// the write model's rules.
/// </remarks>
public class TenantUsersView
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public int UserCount { get; set; }
    public List<UserView> Users { get; set; } = new();

    /// <summary>
    /// The aggregate version this row reflects, or 0 when the tenant was only ever inferred from a
    /// user event that arrived first.
    /// </summary>
    /// <remarks>
    /// Reported rather than hidden. A view built from a user event alone knows the tenant id and
    /// nothing else, and saying <c>company: ""</c> with no explanation would read as "this tenant has
    /// no name" instead of "this row has not caught up yet".
    /// </remarks>
    public int TenantVersion { get; set; }
}

public class UserView
{
    public string UserId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class ListTenantUsers
{
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>An event as the write side recorded it, for replay. See the note on rebuilds.</summary>
public class RecordedEvent
{
    public string Topic { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public long Sequence { get; set; }
}

public class EventLogResponse
{
    public int Count { get; set; }
    public List<RecordedEvent> Events { get; set; } = new();
}
