using System.Collections.Concurrent;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.TwoTier.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TwoTier.Core.TenantService;

/// <summary>The Tenant aggregate's own store. Share-nothing: no other service reads it.</summary>
public class TenantStore
{
    private readonly ConcurrentDictionary<string, string> _tenants = new();

    public string Create(string company)
    {
        var id = $"tnt-{Guid.NewGuid():N}"[..12];
        _tenants[id] = company;
        return id;
    }

    public bool Delete(string tenantId) => _tenants.TryRemove(tenantId, out _);

    public string? CompanyFor(string tenantId) => _tenants.TryGetValue(tenantId, out var c) ? c : null;

    public int Count => _tenants.Count;

    public IReadOnlyCollection<string> Companies => _tenants.Values.ToList();
}

/// <summary>
/// A core service handler: validate, write one aggregate, return. No process logic.
/// </summary>
/// <remarks>
/// This is the whole of what a core service does, and the discipline is what makes the tiering
/// work: the shape of the tenant aggregate can change here without touching any business process,
/// and a business process can change without touching this file.
/// </remarks>
[Message(Topics.TenantCreate)]
public class CreateTenantHandler : IMessageHandler<CreateTenantRequest, TenantCreated>
{
    private readonly TenantStore _store;

    public CreateTenantHandler(TenantStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<TenantCreated>> HandleAsync(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Company))
        {
            return BenzeneResult.ValidationError<TenantCreated>("Company is required.").AsTask();
        }

        return BenzeneResult.Ok(new TenantCreated
        {
            TenantId = _store.Create(request.Company),
            Company = request.Company
        }).AsTask();
    }
}

/// <summary>
/// The compensation for <see cref="CreateTenantHandler"/> — an ordinary topic call, not a rollback.
/// </summary>
/// <remarks>
/// The core service does not know it is being compensated. To it this is just a delete, and that is
/// the point: the saga is the orchestration, the core services do the work.
/// </remarks>
[Message(Topics.TenantDelete)]
public class DeleteTenantHandler : IMessageHandler<DeleteTenantRequest, Acknowledged>
{
    private readonly TenantStore _store;
    private readonly ILogger<DeleteTenantHandler> _logger;

    public DeleteTenantHandler(TenantStore store, ILogger<DeleteTenantHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<IBenzeneResult<Acknowledged>> HandleAsync(DeleteTenantRequest request)
    {
        // A demo switch, and the most important one here: a company whose name starts with "sticky"
        // cannot be deleted. That makes the COMPENSATION fail, which is how a saga reaches
        // PartiallyRolledBack - the one outcome its invariant cannot restore, and the one that must
        // never be retried automatically, because retrying on top of a possibly-applied effect is
        // how you double-charge a customer.
        var company = _store.CompanyFor(request.TenantId);
        if (company != null && company.StartsWith("sticky", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogError("Compensation failed: tenant {TenantId} could not be deleted", request.TenantId);
            return BenzeneResult.ServiceUnavailable<Acknowledged>().AsTask();
        }

        _store.Delete(request.TenantId);
        _logger.LogInformation("Compensated: deleted tenant {TenantId}", request.TenantId);
        return BenzeneResult.Ok(new Acknowledged()).AsTask();
    }
}

/// <summary>Exposed so the smoke test can prove a rolled-back signup left nothing behind.</summary>
[Message("tenant:list")]
[Benzene.Http.HttpEndpoint("GET", "/tenants")]
public class ListTenantsHandler : IMessageHandler<Acknowledged, TenantListResponse>
{
    private readonly TenantStore _store;

    public ListTenantsHandler(TenantStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<TenantListResponse>> HandleAsync(Acknowledged request)
        => BenzeneResult.Ok(new TenantListResponse
        {
            Count = _store.Count,
            Companies = _store.Companies.OrderBy(x => x, StringComparer.Ordinal).ToList()
        }).AsTask();
}

public class TenantListResponse
{
    public int Count { get; set; }
    public List<string> Companies { get; set; } = new();
}
