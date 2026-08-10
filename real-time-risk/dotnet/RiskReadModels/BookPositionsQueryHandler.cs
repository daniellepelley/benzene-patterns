using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Http;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// Serves a book's current positions from the in-memory projection - the cross-cutting query no
/// single core service can answer directly, per docs/patterns/reference-real-time-risk.md §3.
/// </summary>
[Message(Topics.BookPositions)]
[HttpEndpoint("GET", "/books/{book}/positions")]
public class BookPositionsQueryHandler : IMessageHandler<BookPositionsRequest, BookPositionsResponse>
{
    private readonly BookPositionsStore _store;

    public BookPositionsQueryHandler(BookPositionsStore store)
    {
        _store = store;
    }

    public Task<IBenzeneResult<BookPositionsResponse>> HandleAsync(BookPositionsRequest message)
    {
        if (string.IsNullOrWhiteSpace(message.Book))
        {
            return Task.FromResult(BenzeneResult.ValidationError<BookPositionsResponse>("Book is required."));
        }

        return Task.FromResult(BenzeneResult.Ok(_store.Query(message.Book)));
    }
}
