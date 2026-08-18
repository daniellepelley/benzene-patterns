using Benzene.Abstractions;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.Choreography.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.Choreography.Reactions.Analytics;

/// <summary>
/// The fourth reaction — the one added after the other four services were already running.
/// </summary>
/// <remarks>
/// <para>
/// An event handler is an ordinary message handler. There is no separate event API and no subscriber
/// registration — <c>[Message("tenant:created")]</c> is the subscription, and the only thing that
/// makes this an event rather than a command is that nobody is waiting for the answer.
/// </para>
/// <para>
/// <b>This service is the proof of the pattern's central claim, and the way to read it is to look at
/// what is NOT here.</b> Adding it required no change to the emitter, no change to the other three
/// reactions, and no change to any shared topology file — it declares its own queue, binds it to the
/// exchange that already existed, and starts consuming events that were already flowing. It is
/// behind a compose profile so you can watch exactly that happen against a running estate.
/// </para>
/// <para>
/// It also only sees events emitted after it starts. A fanout exchange delivers to the queues bound
/// at publish time, so a late subscriber has no history — the trade for the decoupling. When a new
/// consumer does need the backlog, that is a durable log (Kafka, Kinesis, an event store), not a
/// broker, and it is a different pattern.
/// </para>
/// <para>
/// The response type exists because Benzene's request/response handler interface is what a transport
/// pipeline dispatches to, and its result is what tells the worker to ack or nack. Nothing reads the
/// payload — it is the STATUS that settles the delivery.
/// </para>
/// </remarks>
[Message(Topics.TenantCreated)]
public class RecordSignupOnTenantCreated : IMessageHandler<TenantCreated, Reacted>
{
    private readonly Journal _journal;
    private readonly ICorrelationId _correlationId;
    private readonly ILogger<RecordSignupOnTenantCreated> _logger;

    public RecordSignupOnTenantCreated(Journal journal, ICorrelationId correlationId, ILogger<RecordSignupOnTenantCreated> logger)
    {
        _journal = journal;
        _correlationId = correlationId;
        _logger = logger;
    }

    public Task<IBenzeneResult<Reacted>> HandleAsync(TenantCreated message)
    {
        _journal.Record($"signup: {message.Company} on {message.Plan}", _correlationId.Get());
        _logger.LogInformation("Recorded signup for {Company}", message.Company);
        return BenzeneResult.Ok(new Reacted { What = $"signup: {message.Company}" }).AsTask();
    }
}

/// <summary>Nobody reads this. The status settles the delivery; the payload is for the logs.</summary>
public class Reacted
{
    public string What { get; set; } = string.Empty;
}
