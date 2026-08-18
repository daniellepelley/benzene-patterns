using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Clients;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.TransactionalOutbox.Contracts;
using Benzene.Results;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.TransactionalOutbox.OrdersService;

/// <summary>
/// THE PATTERN. Commits the order and stops.
/// </summary>
/// <remarks>
/// <para>
/// There is no publish here, and that is the entire point. The order table has a change stream, so
/// the committed write <i>is</i> the trigger: the relay reads the stream and publishes
/// <c>order:created</c>. The event is emitted if and only if the write committed - no gap to crash
/// in, no phantom event for a write that rolled back.
/// </para>
/// <para>
/// Compare <see cref="PlaceOrderNaiveHandler"/> below, which does the obvious thing and is quietly
/// unreliable.
/// </para>
/// </remarks>
[Message(Topics.OrderPlace)]
[HttpEndpoint("POST", "/orders")]
public class PlaceOrderHandler : IMessageHandler<PlaceOrderRequest, PlaceOrderResponse>
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _table;

    public PlaceOrderHandler(IAmazonDynamoDB dynamoDb, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _table = configuration["ORDERS_TABLE_NAME"] ?? "orders";
    }

    public async Task<IBenzeneResult<PlaceOrderResponse>> HandleAsync(PlaceOrderRequest request)
    {
        if (request.Total <= 0)
        {
            return BenzeneResult.ValidationError<PlaceOrderResponse>("Total must be positive.");
        }

        var order = new PlaceOrderResponse
        {
            OrderId = $"ord-{Guid.NewGuid():N}"[..12],
            Customer = request.Customer,
            Total = request.Total,
            Path = "cdc"
        };

        await _dynamoDb.PutItemAsync(_table, Item(order));

        // One write. One system. Nothing else to go wrong.
        return BenzeneResult.Ok(order);
    }

    internal static Dictionary<string, AttributeValue> Item(PlaceOrderResponse order) => new()
    {
        ["orderId"] = new AttributeValue { S = order.OrderId },
        ["customer"] = new AttributeValue { S = order.Customer },
        ["total"] = new AttributeValue { N = order.Total.ToString(System.Globalization.CultureInfo.InvariantCulture) }
    };
}

/// <summary>
/// THE BUG, on purpose and reproducibly: write, then publish, and hope nothing happens in between.
/// </summary>
/// <remarks>
/// <para>
/// This handler writes to a table with <b>no</b> change stream and then publishes the event itself -
/// the "obvious" implementation the pattern exists to replace. Send it
/// <c>crashBeforePublish: true</c> and it commits the order and throws before the publish, exactly
/// as a process kill, a network blip or a throttle would. The order is real; the event never
/// happened; nothing downstream will ever hear about it, and no amount of at-least-once delivery
/// helps, because the emit did not occur at all.
/// </para>
/// <para>
/// It exists so the smoke test can <i>demonstrate</i> the gap - orders written, notifications
/// missing - rather than asking a reader to take the paragraph on trust.
/// </para>
/// </remarks>
[Message("order:place-naive")]
[HttpEndpoint("POST", "/orders/naive")]
public class PlaceOrderNaiveHandler : IMessageHandler<PlaceOrderRequest, PlaceOrderResponse>
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly IBenzeneMessageSender _sender;
    private readonly ILogger<PlaceOrderNaiveHandler> _logger;
    private readonly string _table;

    public PlaceOrderNaiveHandler(
        IAmazonDynamoDB dynamoDb, IBenzeneMessageSender sender,
        ILogger<PlaceOrderNaiveHandler> logger, IConfiguration configuration)
    {
        _dynamoDb = dynamoDb;
        _sender = sender;
        _logger = logger;
        _table = configuration["NAIVE_ORDERS_TABLE_NAME"] ?? "orders-naive";
    }

    public async Task<IBenzeneResult<PlaceOrderResponse>> HandleAsync(PlaceOrderRequest request)
    {
        var order = new PlaceOrderResponse
        {
            OrderId = $"ord-{Guid.NewGuid():N}"[..12],
            Customer = request.Customer,
            Total = request.Total,
            Path = "naive"
        };

        // (1) committed
        await _dynamoDb.PutItemAsync(_table, PlaceOrderHandler.Item(order));

        if (request.CrashBeforePublish)
        {
            // (2) ...and here is the gap. In production this is a pod eviction, a deploy, an OOM, or
            // simply the network dropping the publish. The order is committed either way.
            _logger.LogWarning("Order {OrderId} committed; crashing before publish (dual-write gap)", order.OrderId);
            throw new InvalidOperationException("Crashed after commit, before publish.");
        }

        await _sender.SendAsync<OrderCreated, PlaceOrderResponse>(Topics.OrderCreated, new OrderCreated
        {
            OrderId = order.OrderId,
            Customer = order.Customer,
            Total = order.Total,
            // No stream, so no sequence number to use as identity - the handler invents one, and a
            // retry would invent a different one. The consumer's dedupe cannot save this path.
            EventId = $"naive-{Guid.NewGuid():N}"
        });

        return BenzeneResult.Ok(order);
    }
}
