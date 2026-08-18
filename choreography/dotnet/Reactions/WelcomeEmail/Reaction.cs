using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Choreography.Reactions.WelcomeEmail;

/// <summary>
/// Sends the welcome email. The reaction that is allowed to fail, so failure isolation is observable.
/// </summary>
/// <remarks>
/// <para>
/// An event handler is an ordinary message handler. There is no separate event API and no subscriber
/// registration — <c>[Message("tenant:created")]</c> is the subscription, and the only thing that
/// makes this an event rather than a command is that nobody is waiting for the answer.
/// </para>
/// <para>
/// A company whose name starts with <c>bounce</c> makes this reaction fail. That is the demo's
/// mail-server-is-down switch, and what it demonstrates is the thing choreography gets wrong most
/// often in people's heads: <b>the tenant is still created, and the other two reactions still ran</b>.
/// There is no rollback here and there is not supposed to be one. An invariant that must hold across
/// services belongs in an orchestrated saga; this is a reaction that must merely happen, and when it
/// does not happen, it retries on its own.
/// </para>
/// <para>
/// Returning a failure status rather than throwing is what tells the worker to nack. Under this
/// worker's bounded requeue a first failure is requeued and a second is nacked without requeue — to
/// the dead-letter exchange if the queue has one, and dropped otherwise, which is the case here. A
/// production deployment configures a DLX and a redelivery policy on the broker; leaving that out
/// keeps the example honest about where the retry limit actually lives.
/// </para>
/// <para>
/// The response type exists because Benzene's request/response handler interface is what a transport
/// pipeline dispatches to, and its result is what tells the worker to ack or nack. Nothing reads the
/// payload — it is the STATUS that settles the delivery.
/// </para>
/// </remarks>
[Message(Topics.TenantCreated)]
public class SendWelcomeEmailOnTenantCreated : IMessageHandler<TenantCreated, Reacted>
{
    private readonly Journal _journal;
    private readonly ICorrelationId _correlationId;
    private readonly ILogger<SendWelcomeEmailOnTenantCreated> _logger;

    public SendWelcomeEmailOnTenantCreated(Journal journal, ICorrelationId correlationId, ILogger<SendWelcomeEmailOnTenantCreated> logger)
    {
        _journal = journal;
        _correlationId = correlationId;
        _logger = logger;
    }

    public Task<IBenzeneResult<Reacted>> HandleAsync(TenantCreated message)
    {
        if (message.Company.StartsWith("bounce", StringComparison.OrdinalIgnoreCase))
        {
            _journal.Record($"FAILED to email {message.Email}", _correlationId.Get());
            _logger.LogError("Mail server rejected {Email}", message.Email);
            return BenzeneResult.ServiceUnavailable<Reacted>("Mail server unavailable.").AsTask();
        }

        _journal.Record($"welcomed {message.Email}", _correlationId.Get());
        _logger.LogInformation("Sent welcome email to {Email}", message.Email);
        return BenzeneResult.Ok(new Reacted { What = $"welcomed {message.Email}" }).AsTask();
    }
}

/// <summary>Nobody reads this. The status settles the delivery; the payload is for the logs.</summary>
public class Reacted
{
    public string What { get; set; } = string.Empty;
}
