using System.Collections.Concurrent;
using Benzene.Patterns.RealTimeRisk.Contracts;

namespace Benzene.Patterns.RealTimeRisk.RiskReadModels;

/// <summary>
/// The projection this service builds: per book, per symbol net position + realized cash, folded from
/// <see cref="TradeBooked"/> events as <see cref="TradeStreamProjector"/> consumes them off the ledger's
/// DynamoDB Stream. In-memory only for this first slice (resets on restart/redeploy) - see
/// real-time-risk/README.md for why a real store isn't needed to prove the pattern yet.
/// </summary>
public class BookPositionsStore
{
    private class Book
    {
        public readonly ConcurrentDictionary<string, PositionView> Positions = new();
        public readonly HashSet<long> AppliedVersions = new();
        public long ProjectedThroughVersion;
    }

    private readonly ConcurrentDictionary<string, Book> _books = new();
    private readonly object _gate = new();

    /// <summary>
    /// Applies one <see cref="TradeBooked"/> event to the book's projection. Idempotent by
    /// (book, version): DynamoDB Streams is at-least-once, so a redelivered record must not be
    /// double-applied.
    /// </summary>
    public void Apply(TradeBooked trade, long version)
    {
        lock (_gate)
        {
            var book = _books.GetOrAdd(trade.Book, _ => new Book());
            if (!book.AppliedVersions.Add(version))
            {
                return;
            }

            var current = book.Positions.GetValueOrDefault(trade.Symbol, new PositionView { Symbol = trade.Symbol });
            var signedQuantity = trade.Side == TradeSide.Buy ? trade.Quantity : -trade.Quantity;
            var cashFlow = trade.Side == TradeSide.Sell ? trade.Quantity * trade.Price : -(trade.Quantity * trade.Price);

            book.Positions[trade.Symbol] = new PositionView
            {
                Symbol = trade.Symbol,
                NetQuantity = current.NetQuantity + signedQuantity,
                RealizedCash = current.RealizedCash + cashFlow
            };

            if (version > book.ProjectedThroughVersion)
            {
                book.ProjectedThroughVersion = version;
            }
        }
    }

    /// <summary>
    /// Reads every book's position in one symbol - the inverse of <see cref="Query"/>'s "one book,
    /// every symbol", and what the Valuation Service asks when a bar closes ("who is exposed to
    /// AAPL?"). An unknown symbol returns an empty list.
    /// </summary>
    /// <remarks>
    /// Deliberately a scan across the existing per-book dictionaries rather than a second,
    /// symbol-keyed index: this is one read path added to answer one downstream question, and a demo
    /// with a handful of books scans them in microseconds. A real deployment with thousands of books
    /// would maintain the inverted index at projection time (in <see cref="Apply"/>) instead - that
    /// is a storage-model change, not a query change, and it belongs with the "swap the in-memory
    /// store for a real one" work this projection already owes.
    /// </remarks>
    public SymbolPositionsResponse QueryBySymbol(string symbol)
    {
        lock (_gate)
        {
            var books = _books
                .Where(entry => entry.Value.Positions.ContainsKey(symbol))
                .Select(entry => new BookPositionView
                {
                    Book = entry.Key,
                    NetQuantity = entry.Value.Positions[symbol].NetQuantity,
                    RealizedCash = entry.Value.Positions[symbol].RealizedCash
                })
                .OrderBy(view => view.Book, StringComparer.Ordinal)
                .ToList();

            return new SymbolPositionsResponse { Symbol = symbol, Books = books };
        }
    }

    /// <summary>Reads a book's current projection. An unknown book returns an empty (zero-trade) result.</summary>
    public BookPositionsResponse Query(string bookId)
    {
        if (!_books.TryGetValue(bookId, out var book))
        {
            return new BookPositionsResponse { Book = bookId, Positions = new List<PositionView>(), ProjectedThroughVersion = 0 };
        }

        lock (_gate)
        {
            return new BookPositionsResponse
            {
                Book = bookId,
                Positions = book.Positions.Values.OrderBy(p => p.Symbol).ToList(),
                ProjectedThroughVersion = book.ProjectedThroughVersion
            };
        }
    }
}
