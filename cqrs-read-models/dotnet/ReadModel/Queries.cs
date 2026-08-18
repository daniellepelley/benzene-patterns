using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.Cqrs.Contracts;
using Benzene.Results;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.Cqrs.ReadModel;

/// <summary>
/// The read side: one indexed read, no fan-out.
/// </summary>
/// <remarks>
/// <para>
/// Compare what this would take without a read model: ask the Tenant service for the tenant, ask the
/// User service for its users, stitch the two together — per request, forever. The join has been
/// moved from query time to event time and paid for once, when the data changed.
/// </para>
/// <para>
/// A miss is <c>not-found</c> and means one of two different things: no such tenant, or the event has
/// not arrived yet. The read model genuinely cannot tell them apart, and pretending otherwise is the
/// mistake — a caller that needs to know reads the Tenant service, which is the authority and is
/// always current.
/// </para>
/// </remarks>
[Message(Topics.TenantUsersList)]
[HttpEndpoint("GET", "/tenants/{tenantId}")]
public class ListTenantUsersHandler : IMessageHandler<ListTenantUsers, TenantUsersView>
{
    private readonly ReadStore _view;

    public ListTenantUsersHandler(ReadStore view)
    {
        _view = view;
    }

    public Task<IBenzeneResult<TenantUsersView>> HandleAsync(ListTenantUsers query)
    {
        var view = _view.Get(query.TenantId);
        return view == null
            ? BenzeneResult.NotFound<TenantUsersView>().AsTask()
            : BenzeneResult.Ok(view).AsTask();
    }
}

[Message("tenants:list")]
[HttpEndpoint("GET", "/tenants")]
public class ListAllHandler : IMessageHandler<Void, TenantListResponse>
{
    private readonly ReadStore _view;

    public ListAllHandler(ReadStore view)
    {
        _view = view;
    }

    public Task<IBenzeneResult<TenantListResponse>> HandleAsync(Void request)
    {
        var all = _view.All();
        return BenzeneResult.Ok(new TenantListResponse { Count = all.Count, Tenants = all }).AsTask();
    }
}

public class TenantListResponse
{
    public int Count { get; set; }
    public List<TenantUsersView> Tenants { get; set; } = new();
}
