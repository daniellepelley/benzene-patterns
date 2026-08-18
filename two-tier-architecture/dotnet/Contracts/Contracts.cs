namespace Benzene.Patterns.TwoTier.Contracts;

/// <summary>
/// One topic per aggregate operation, plus the orchestrated process on top.
/// </summary>
/// <remarks>
/// Read the split here and the architecture is already visible: the <c>tenant:*</c>, <c>user:*</c>
/// and <c>billing:*</c> topics are CRUD on one aggregate each - the core tier - and
/// <c>signup:start</c> is a business PROCESS that touches all three. Nothing in the core tier knows
/// signup exists.
/// </remarks>
public static class Topics
{
    // ── Core tier: data ─────────────────────────────────────────────────────────────────────────
    public const string TenantCreate = "tenant:create";
    public const string TenantDelete = "tenant:delete";
    public const string UserCreate = "user:create";
    public const string UserDelete = "user:delete";
    public const string BillingSetup = "billing:setup";
    public const string BillingTeardown = "billing:teardown";

    // ── Orchestrator tier: process ──────────────────────────────────────────────────────────────
    public const string SignupStart = "signup:start";
}

public class CreateTenantRequest
{
    public string Company { get; set; } = string.Empty;
}

public class TenantCreated
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
}

public class DeleteTenantRequest
{
    public string TenantId { get; set; } = string.Empty;
}

public class CreateUserRequest
{
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class UserCreated
{
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public class DeleteUserRequest
{
    public string UserId { get; set; } = string.Empty;
}

public class SetupBillingRequest
{
    public string Company { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
}

public class BillingAccountCreated
{
    public string AccountId { get; set; } = string.Empty;
    public string Plan { get; set; } = string.Empty;
}

public class TeardownBillingRequest
{
    public string AccountId { get; set; } = string.Empty;
}

/// <summary>An empty response for the delete/teardown compensations.</summary>
public class Acknowledged
{
    public bool Ok { get; set; } = true;
}

/// <summary>The orchestrated process: sign a company up across all three core services.</summary>
public class SignupRequest
{
    public string Company { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;

    /// <summary>
    /// <c>standard</c> or <c>enterprise</c>. Anything else is rejected by Billing — which is how a
    /// caller makes stage 1 fail on demand, so the rollback is observable.
    /// </summary>
    public string Plan { get; set; } = "standard";
}

public class SignupResponse
{
    public string Outcome { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string AccountId { get; set; } = string.Empty;

    /// <summary>Which stage failed, when one did. Null on success.</summary>
    public int? FailedStage { get; set; }

    /// <summary>The failing step's Benzene status, so the caller learns WHY, not just that.</summary>
    public string? FailureStatus { get; set; }

    /// <summary>
    /// Effects whose compensation itself failed — the orphans a <c>PartiallyRolledBack</c> leaves.
    /// </summary>
    /// <remarks>
    /// Named rather than counted. This is the one outcome the saga's invariant could not restore, so
    /// somebody has to go and look; a number would not tell them where.
    /// </remarks>
    public List<string> Orphans { get; set; } = new();
}
