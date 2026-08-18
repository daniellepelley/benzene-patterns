using System.Collections.Concurrent;
using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;
using Void = Benzene.Abstractions.Results.Void;

namespace Benzene.Patterns.Choreography.Emitter;

/// <summary>The emitter's own store. It owns the tenant; the reactions own their own consequences.</summary>
public class TenantStore
{
    private readonly ConcurrentDictionary<string, string> _tenants = new();

    public string Create(string company)
    {
        var id = $"tnt-{Guid.NewGuid():N}"[..12];
        _tenants[id] = company;
        return id;
    }

    public int Count => _tenants.Count;
}

/// <summary>
/// Does its own work, announces that it happened, and stops.
/// </summary>
/// <remarks>
/// <para>
/// The whole of choreography is in the <c>SendAsync&lt;TenantCreated, Void&gt;</c> line. The
/// <c>Void</c> response type is what makes it an event rather than a command: there is nothing to
/// wait for and nothing to come back. The handler does not know whether one service reacts or four,
/// and it returns to its caller either way.
/// </para>
/// <para>
/// It returns <c>Accepted</c>, not <c>Ok</c>, and the distinction is load-bearing. The tenant exists;
/// the welcome email, the warmed cache and the trial clock have not happened yet and may not have
/// happened by the time the caller reads the response. Saying <c>Ok</c> would be claiming a
/// completeness choreography deliberately does not offer.
/// </para>
/// </remarks>
[Message("tenant:create")]
[HttpEndpoint("POST", "/tenants")]
public class CreateTenantHandler : IMessageHandler<CreateTenantRequest, TenantAccepted>
{
    private readonly IBenzeneMessageSender _sender;
    private readonly TenantStore _store;
    private readonly ICorrelationId _correlationId;
    private readonly ILogger<CreateTenantHandler> _logger;

    public CreateTenantHandler(IBenzeneMessageSender sender, TenantStore store, ICorrelationId correlationId,
        ILogger<CreateTenantHandler> logger)
    {
        _sender = sender;
        _store = store;
        _correlationId = correlationId;
        _logger = logger;
    }

    public async Task<IBenzeneResult<TenantAccepted>> HandleAsync(CreateTenantRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Company))
        {
            return BenzeneResult.ValidationError<TenantAccepted>("Company is required.");
        }

        var tenantId = _store.Create(request.Company);
        var @event = new TenantCreated
        {
            TenantId = tenantId,
            Company = request.Company,
            Email = request.Email,
            Plan = request.Plan
        };

        // Publishing the SAME event more than once is the demo's stand-in for at-least-once delivery.
        // Every real broker here - SNS, SQS, EventBridge, RabbitMQ - can redeliver, so a reaction that
        // is not idempotent is not a reaction that works; this just makes the redelivery happen on
        // demand instead of on a bad day.
        var times = Math.Clamp(request.EmitTimes, 1, 10);
        for (var i = 0; i < times; i++)
        {
            await _sender.SendAsync<TenantCreated, Void>(Topics.TenantCreated, @event);
        }

        _logger.LogInformation("Emitted {Topic} for {TenantId} x{Times}", Topics.TenantCreated, tenantId, times);

        // Accepted, not Ok: the tenant exists, and nothing else is promised. See the class remarks.
        return BenzeneResult.Accepted(new TenantAccepted
        {
            TenantId = tenantId,
            Company = request.Company,
            Emitted = times,
            CorrelationId = _correlationId.Get()
        });
    }
}

/// <summary>Exposed so the smoke test can show the emitter succeeded whatever the reactions did.</summary>
[Message("tenant:list")]
[HttpEndpoint("GET", "/tenants")]
public class ListTenantsHandler : IMessageHandler<Void, TenantCountResponse>
{
    private readonly TenantStore _store;

    public ListTenantsHandler(TenantStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<TenantCountResponse>> HandleAsync(Void request)
        => BenzeneResult.Ok(new TenantCountResponse { Count = _store.Count }).AsTask();
}

public class TenantCountResponse
{
    public int Count { get; set; }
}
