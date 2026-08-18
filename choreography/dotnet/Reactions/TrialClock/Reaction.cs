using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Choreography.Reactions.TrialClock;

/// <summary>
/// Starts the trial clock — but only for the plans that have one.
/// </summary>
/// <remarks>
/// <para>
/// An event handler is an ordinary message handler. There is no separate event API and no subscriber
/// registration — <c>[Message("tenant:created")]</c> is the subscription, and the only thing that
/// makes this an event rather than a command is that nobody is waiting for the answer.
/// </para>
/// <para>
/// A reaction gets to decide it has nothing to do. An enterprise signup has no trial, so this
/// handler returns success without recording anything: <b>it consumed the event and correctly did
/// nothing</b>. That decision belongs here, in the service that owns the concept of a trial, and
/// putting it in the emitter instead is how an emitter slowly learns about its consumers.
/// </para>
/// <para>
/// The response type exists because Benzene's request/response handler interface is what a transport
/// pipeline dispatches to, and its result is what tells the worker to ack or nack. Nothing reads the
/// payload — it is the STATUS that settles the delivery.
/// </para>
/// </remarks>
[Message(Topics.TenantCreated)]
public class StartTrialOnTenantCreated : IMessageHandler<TenantCreated, Reacted>
{
    private readonly Journal _journal;
    private readonly ICorrelationId _correlationId;
    private readonly ILogger<StartTrialOnTenantCreated> _logger;

    public StartTrialOnTenantCreated(Journal journal, ICorrelationId correlationId, ILogger<StartTrialOnTenantCreated> logger)
    {
        _journal = journal;
        _correlationId = correlationId;
        _logger = logger;
    }

    public Task<IBenzeneResult<Reacted>> HandleAsync(TenantCreated message)
    {
        if (!message.Plan.Equals("standard", StringComparison.OrdinalIgnoreCase))
        {
            // Consumed, understood, and deliberately no-op. Not a failure - there is no trial on an
            // enterprise plan, and the emitter is not the place to know that.
            _logger.LogInformation("No trial for {Plan} plan, tenant {TenantId}", message.Plan, message.TenantId);
            return BenzeneResult.Ok(new Reacted { What = "no trial for this plan" }).AsTask();
        }

        _journal.Record($"trial started for {message.TenantId} (14 days)", _correlationId.Get());
        _logger.LogInformation("Started 14-day trial for {TenantId}", message.TenantId);
        return BenzeneResult.Ok(new Reacted { What = $"trial started for {message.TenantId}" }).AsTask();
    }
}

/// <summary>Nobody reads this. The status settles the delivery; the payload is for the logs.</summary>
public class Reacted
{
    public string What { get; set; } = string.Empty;
}
