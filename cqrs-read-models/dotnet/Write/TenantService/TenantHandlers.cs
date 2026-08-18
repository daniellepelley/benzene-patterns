using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.Cqrs.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.Cqrs.Write.TenantService;

/// <summary>
/// The Tenant aggregate: normalized, authoritative, and deliberately ignorant of users.
/// </summary>
/// <remarks>
/// Nothing here can answer "this tenant and its users", and that is not an oversight to be fixed in
/// this service. The directional rule says the parent must not know its children exist, and this is
/// the service that would have to break it. The read model exists because this restriction is worth
/// keeping.
/// </remarks>
public class TenantStore
{
    private readonly ConcurrentDictionary<string, (string Company, int Version)> _tenants = new();

    public (string TenantId, int Version) Create(string company)
    {
        var id = $"tnt-{Guid.NewGuid():N}"[..12];
        _tenants[id] = (company, 1);
        return (id, 1);
    }

    public int? Rename(string tenantId, string company)
    {
        if (!_tenants.TryGetValue(tenantId, out var current))
        {
            return null;
        }

        var next = current.Version + 1;
        _tenants[tenantId] = (company, next);
        return next;
    }

    public (string Company, int Version)? Get(string tenantId)
        => _tenants.TryGetValue(tenantId, out var t) ? t : null;
}

[Message(Topics.TenantCreate)]
[HttpEndpoint("POST", "/tenants")]
public class CreateTenantHandler : IMessageHandler<CreateTenantRequest, TenantAccepted>
{
    private readonly TenantStore _store;
    private readonly EventLog _log;
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<CreateTenantHandler> _logger;

    public CreateTenantHandler(TenantStore store, EventLog log, IBenzeneMessageSender sender,
        ILogger<CreateTenantHandler> logger)
    {
        _store = store;
        _log = log;
        _sender = sender;
        _logger = logger;
    }

    public async Task<IBenzeneResult<TenantAccepted>> HandleAsync(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Company))
        {
            return BenzeneResult.ValidationError<TenantAccepted>("Company is required.");
        }

        var (tenantId, version) = _store.Create(request.Company);
        var @event = new TenantCreated { TenantId = tenantId, Company = request.Company, Version = version };

        // Recorded BEFORE it is published, and recorded once however many times it is published. The
        // log is the replay source; the publishes are the live delivery. Conflating the two is how a
        // rebuild ends up reproducing the duplicates instead of the history.
        _log.Append(Topics.TenantCreated, @event);

        var times = Math.Clamp(request.EmitTimes, 1, 10);
        for (var i = 0; i < times; i++)
        {
            await _sender.SendAsync<TenantCreated, Void>(Topics.TenantCreated, @event);
        }

        _logger.LogInformation("Created tenant {TenantId} v{Version}", tenantId, version);

        // Accepted, not Ok: the tenant exists HERE, and the read model has not seen it yet. Saying Ok
        // would imply a completeness the write side cannot speak for.
        return BenzeneResult.Accepted(new TenantAccepted
        {
            TenantId = tenantId,
            Company = request.Company,
            Version = version,
            Emitted = times
        });
    }
}

[Message(Topics.TenantRename)]
[HttpEndpoint("POST", "/renames")]
public class RenameTenantHandler : IMessageHandler<RenameTenantRequest, TenantAccepted>
{
    private readonly TenantStore _store;
    private readonly EventLog _log;
    private readonly IBenzeneMessageSender _sender;

    public RenameTenantHandler(TenantStore store, EventLog log, IBenzeneMessageSender sender)
    {
        _store = store;
        _log = log;
        _sender = sender;
    }

    public async Task<IBenzeneResult<TenantAccepted>> HandleAsync(RenameTenantRequest request)
    {
        var version = _store.Rename(request.TenantId, request.Company);
        if (version == null)
        {
            return BenzeneResult.NotFound<TenantAccepted>();
        }

        var @event = new TenantRenamed
        {
            TenantId = request.TenantId,
            Company = request.Company,
            Version = version.Value
        };

        _log.Append(Topics.TenantRenamed, @event);
        await _sender.SendAsync<TenantRenamed, Void>(Topics.TenantRenamed, @event);

        return BenzeneResult.Accepted(new TenantAccepted
        {
            TenantId = request.TenantId,
            Company = request.Company,
            Version = version.Value,
            Emitted = 1
        });
    }
}

/// <summary>
/// The authority read: always current, single-aggregate, and the right place to serve a
/// read-your-writes screen from.
/// </summary>
/// <remarks>
/// CQRS is a per-query decision, not a routing policy. Right after a write this answers correctly
/// and the read model may not; for a cross-aggregate query it cannot answer at all and the read
/// model can. Routing everything through the read model reflexively is the common mistake.
/// </remarks>
[Message("tenant:get")]
[HttpEndpoint("GET", "/tenants/{tenantId}")]
public class GetTenantHandler : IMessageHandler<GetTenantRequest, TenantAccepted>
{
    private readonly TenantStore _store;

    public GetTenantHandler(TenantStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<TenantAccepted>> HandleAsync(GetTenantRequest request)
    {
        var tenant = _store.Get(request.TenantId);
        return tenant == null
            ? BenzeneResult.NotFound<TenantAccepted>().AsTask()
            : BenzeneResult.Ok(new TenantAccepted
            {
                TenantId = request.TenantId,
                Company = tenant.Value.Company,
                Version = tenant.Value.Version
            }).AsTask();
    }
}

public class GetTenantRequest
{
    public string TenantId { get; set; } = string.Empty;
}

/// <summary>The replay source. See <see cref="EventLog"/> for what this stands in for.</summary>
[Message("tenant:events")]
[HttpEndpoint("GET", "/events")]
public class ReadEventLogHandler : IMessageHandler<Void, EventLogResponse>
{
    private readonly EventLog _log;

    public ReadEventLogHandler(EventLog log)
    {
        _log = log;
    }

    public Task<IBenzeneResult<EventLogResponse>> HandleAsync(Void request)
        => BenzeneResult.Ok(_log.Read()).AsTask();
}
