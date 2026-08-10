using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.EventSourcing;
using Benzene.Http;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.TradeLedger;

/// <summary>
/// Books a trade: validates it and appends a <see cref="TradeBooked"/> event to the book's ledger
/// stream (stream id = book id). Optimistic concurrency is handled by reading the stream's current
/// version immediately before appending - fine for this demo's traffic; a high-contention book would
/// instead want the caller to supply the version it last saw and retry on
/// <see cref="EventStoreConcurrencyException"/>.
/// </summary>
[Message(Topics.BookTrade)]
[HttpEndpoint("POST", "/trades")]
public class BookTradeHandler : IMessageHandler<BookTradeRequest, BookTradeResponse>
{
    private readonly IEventStore _eventStore;

    public BookTradeHandler(IEventStore eventStore)
    {
        _eventStore = eventStore;
    }

    public async Task<IBenzeneResult<BookTradeResponse>> HandleAsync(BookTradeRequest message)
    {
        if (string.IsNullOrWhiteSpace(message.Book) || string.IsNullOrWhiteSpace(message.Symbol)
            || message.Quantity <= 0 || message.Price <= 0)
        {
            return BenzeneResult.ValidationError<BookTradeResponse>(
                "Book, Symbol, Quantity (> 0) and Price (> 0) are required.");
        }

        var streamId = message.Book;
        var history = await _eventStore.ReadAsync(streamId);
        var expectedVersion = history.Count == 0 ? 0 : history[^1].Version;

        var tradeBooked = new TradeBooked
        {
            TradeId = Guid.NewGuid(),
            Book = message.Book,
            Symbol = message.Symbol,
            Side = message.Side,
            Quantity = message.Quantity,
            Price = message.Price,
            BookedAt = DateTimeOffset.UtcNow
        };

        var payload = System.Text.Json.JsonSerializer.Serialize(tradeBooked);
        var newVersion = await _eventStore.AppendAsync(
            streamId,
            expectedVersion,
            new[] { new EventEnvelope(Topics.TradeBookedEventType, payload) });

        return BenzeneResult.Ok(new BookTradeResponse
        {
            TradeId = tradeBooked.TradeId,
            Book = message.Book,
            Version = newVersion
        });
    }
}
