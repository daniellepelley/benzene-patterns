namespace Benzene.Patterns.Choreography.Contracts;

/// <summary>
/// One topic. That is the whole of the coupling in this example.
/// </summary>
/// <remarks>
/// Compare with the two-tier example, whose orchestrator names six topics because it directs six
/// operations. Here the emitter names ONE - the event it publishes - and has no idea how many
/// services react to it. Adding a reaction adds a consumer; this file does not change, and neither
/// does the emitter.
/// </remarks>
public static class WireHeaders
{
    /// <summary>
    /// The correlation-id header key, named explicitly on both sides rather than left to a default.
    /// </summary>
    /// <remarks>
    /// <c>docs/specification/wire-contracts.md</c> §1.1 uses <c>x-correlation-id</c>, and the pinned
    /// Benzene.Clients (0.0.2-alpha.4) still defaults its outbound stamping middleware to
    /// <c>correlationId</c> — a mismatch later releases close with a shared
    /// <c>CorrelationHeaderDefaults</c>. Naming the key at both ends is the version-independent fix
    /// and, in a real estate, the right habit anyway: the key is part of the wire contract.
    /// </remarks>
    public const string CorrelationId = "x-correlation-id";
}

public static class Topics
{
    /// <summary>A domain event, past tense: it announces what happened, it does not ask for anything.</summary>
    public const string TenantCreated = "tenant:created";
}

/// <summary>
/// The event payload — a published contract, evolved as carefully as any request type.
/// </summary>
/// <remarks>
/// Four services deserialize this, and the emitter knows about none of them. That is the trade
/// choreography makes: cheap to add a reaction, expensive to change the event. Add fields; do not
/// repurpose or remove them, and give a breaking change a new topic version rather than redefining
/// this one under everyone's feet.
/// </remarks>
public class TenantCreated
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = "standard";
}

/// <summary>The request that causes the event. Deliberately not the event itself.</summary>
public class CreateTenantRequest
{
    public string Company { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Plan { get; set; } = "standard";

    /// <summary>
    /// How many times to publish the identical event. The demo's stand-in for at-least-once
    /// delivery: brokers redeliver, and this makes that happen on demand so a reader can watch the
    /// reactions de-duplicate rather than take it on trust.
    /// </summary>
    public int EmitTimes { get; set; } = 1;
}

/// <summary>What the emitter tells its caller. Note what is missing: anything about the reactions.</summary>
public class TenantAccepted
{
    public string TenantId { get; set; } = string.Empty;
    public string Company { get; set; } = string.Empty;

    /// <summary>How many copies of the event were published — 1 unless the caller asked for more.</summary>
    public int Emitted { get; set; }

    /// <summary>
    /// The correlation id the event carried. Returned so the smoke test can check the same id turns
    /// up in every reaction's journal — the concrete, checkable form of "trace context survives the
    /// hop", which is what lets the mesh draw the choreography graph.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;
}
