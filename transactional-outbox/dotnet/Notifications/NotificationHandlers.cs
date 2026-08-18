using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.TransactionalOutbox.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TransactionalOutbox.Notifications;

/// <summary>
/// Reacts to <c>order:created</c>. Idempotently.
/// </summary>
/// <remarks>
/// It has no idea the event came off a change stream - it is ordinary choreography from here on,
/// which is the point of putting the relay in the middle. A consumer that had to know about
/// DynamoDB Streams would have the coupling the pattern removes.
/// </remarks>
[Message(Topics.OrderCreated)]
public class OrderCreatedHandler : IMessageHandler<OrderCreated, PlaceOrderResponse>
{
    private readonly NotificationStore _store;
    private readonly ILogger<OrderCreatedHandler> _logger;

    public OrderCreatedHandler(NotificationStore store, ILogger<OrderCreatedHandler> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<IBenzeneResult<PlaceOrderResponse>> HandleAsync(OrderCreated request)
    {
        if (_store.Record(request))
        {
            _logger.LogInformation("Notified customer {Customer} about order {OrderId}", request.Customer, request.OrderId);
        }
        else
        {
            // A repeat is expected, not an error. The relay redelivers after a failed publish, and
            // the correct answer to a redelivery is a success the sender can stop retrying on -
            // reporting a failure here would make the relay retry a message that HAS taken effect.
            _logger.LogInformation("Ignoring a repeat delivery of event {EventId}", request.EventId);
        }

        return BenzeneResult.Ok(new PlaceOrderResponse
        {
            OrderId = request.OrderId,
            Customer = request.Customer,
            Total = request.Total
        }).AsTask();
    }
}

/// <summary>What this service has been told about, for the smoke test and for a reader.</summary>
[Message("notifications:list")]
[HttpEndpoint("GET", "/notifications")]
public class ListNotificationsHandler : IMessageHandler<NotificationsResponse, NotificationsResponse>
{
    private readonly NotificationStore _store;

    public ListNotificationsHandler(NotificationStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<NotificationsResponse>> HandleAsync(NotificationsResponse request)
        => BenzeneResult.Ok(_store.Snapshot()).AsTask();
}
