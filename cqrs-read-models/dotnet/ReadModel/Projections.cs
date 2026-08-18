using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.Cqrs.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// The projection side: three event handlers, each one a fold into the view.
/// </summary>
/// <remarks>
/// These are ordinary message handlers on the RabbitMQ pipeline — the read model is not a special
/// kind of service, it is a service whose inbound messages happen to be other people's events. It
/// subscribes to <c>tenant:*</c> and <c>user:*</c> without either of those services knowing, which is
/// why a new view can be added to a live estate without a write-side deployment.
/// </remarks>
[Message(Topics.TenantCreated)]
public class ProjectTenantCreated : IMessageHandler<TenantCreated, Projected>
{
    private readonly ReadStore _view;
    private readonly ILogger<ProjectTenantCreated> _logger;

    public ProjectTenantCreated(ReadStore view, ILogger<ProjectTenantCreated> logger)
    {
        _view = view;
        _logger = logger;
    }

    public Task<IBenzeneResult<Projected>> HandleAsync(TenantCreated e)
    {
        _view.UpsertTenant(e.TenantId, e.Company, e.Version);
        _logger.LogInformation("Projected tenant {TenantId} v{Version}", e.TenantId, e.Version);
        return BenzeneResult.Ok(new Projected()).AsTask();
    }
}

[Message(Topics.TenantRenamed)]
public class ProjectTenantRenamed : IMessageHandler<TenantRenamed, Projected>
{
    private readonly ReadStore _view;

    public ProjectTenantRenamed(ReadStore view)
    {
        _view = view;
    }

    public Task<IBenzeneResult<Projected>> HandleAsync(TenantRenamed e)
    {
        _view.UpsertTenant(e.TenantId, e.Company, e.Version);
        return BenzeneResult.Ok(new Projected()).AsTask();
    }
}

/// <summary>
/// The join the write side cannot do: a user lands in its tenant's row.
/// </summary>
/// <remarks>
/// Nothing in the write model may hold these two together — the Tenant service has never heard of
/// users, by rule. This handler performs that join once, at EVENT time, so the query does not have
/// to perform it on every read.
/// </remarks>
[Message(Topics.UserCreated)]
public class ProjectUserOntoTenant : IMessageHandler<UserCreated, Projected>
{
    private readonly ReadStore _view;
    private readonly ILogger<ProjectUserOntoTenant> _logger;

    public ProjectUserOntoTenant(ReadStore view, ILogger<ProjectUserOntoTenant> logger)
    {
        _view = view;
        _logger = logger;
    }

    public Task<IBenzeneResult<Projected>> HandleAsync(UserCreated e)
    {
        _view.AddUser(e.TenantId, e.UserId, e.Email);
        _logger.LogInformation("Projected user {UserId} onto tenant {TenantId}", e.UserId, e.TenantId);
        return BenzeneResult.Ok(new Projected()).AsTask();
    }
}

/// <summary>
/// Nothing reads this. Its STATUS is what settles the delivery: success acks, failure nacks and the
/// broker redelivers — which is why a projection handler needs a result type at all.
/// </summary>
public class Projected
{
    public bool Ok { get; set; } = true;
}
