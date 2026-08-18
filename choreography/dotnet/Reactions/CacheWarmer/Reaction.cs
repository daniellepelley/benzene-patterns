using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Choreography.Reactions.CacheWarmer;

/// <summary>
/// Warms this tenant's cache entry. Independent of every other reaction and of the emitter.
/// </summary>
/// <remarks>
/// <para>
/// An event handler is an ordinary message handler. There is no separate event API and no subscriber
/// registration — <c>[Message("tenant:created")]</c> is the subscription, and the only thing that
/// makes this an event rather than a command is that nobody is waiting for the answer.
/// </para>
/// <para>
/// Nothing here refers to the welcome email or the trial clock, and nothing sequences them. All
/// three consume their own queue off the same fanout exchange, so a slow or failing sibling holds up
/// its own queue and nobody else's — which is why the exchange is a fanout and not a work queue.
/// </para>
/// <para>
/// The response type exists because Benzene's request/response handler interface is what a transport
/// pipeline dispatches to, and its result is what tells the worker to ack or nack. Nothing reads the
/// payload — it is the STATUS that settles the delivery.
/// </para>
/// </remarks>
[Message(Topics.TenantCreated)]
public class WarmCacheOnTenantCreated : IMessageHandler<TenantCreated, Reacted>
{
    private readonly Journal _journal;
    private readonly ICorrelationId _correlationId;
    private readonly ILogger<WarmCacheOnTenantCreated> _logger;

    public WarmCacheOnTenantCreated(Journal journal, ICorrelationId correlationId, ILogger<WarmCacheOnTenantCreated> logger)
    {
        _journal = journal;
        _correlationId = correlationId;
        _logger = logger;
    }

    public Task<IBenzeneResult<Reacted>> HandleAsync(TenantCreated message)
    {
        _journal.Record($"warmed {message.TenantId}", _correlationId.Get());
        _logger.LogInformation("Warmed cache for {TenantId}", message.TenantId);
        return BenzeneResult.Ok(new Reacted { What = $"warmed {message.TenantId}" }).AsTask();
    }
}

/// <summary>Nobody reads this. The status settles the delivery; the payload is for the logs.</summary>
public class Reacted
{
    public string What { get; set; } = string.Empty;
}
