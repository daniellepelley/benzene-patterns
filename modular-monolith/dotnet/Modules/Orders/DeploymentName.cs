namespace Benzene.Patterns.ModularMonolith.Modules.Orders;

/// <summary>
/// What the host calls this deployment - <c>monolith</c> or <c>extracted</c>.
/// </summary>
/// <remarks>
/// A one-field type registered by the host rather than an <c>IConfiguration</c> read, deliberately.
/// A module that reaches into the host's configuration system has a dependency on the host, and the
/// claim this example makes is that it has none: the same assembly is mounted by two different hosts
/// and cannot tell them apart. It is told its label, the same way it is told its collaborators'
/// addresses - which is to say, not at all, since the routing table does that.
/// </remarks>
public sealed record DeploymentName(string Value);
