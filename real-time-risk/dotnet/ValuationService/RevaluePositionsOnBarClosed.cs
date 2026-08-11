using Benzene.Abstractions.MessageHandlers;
using Benzene.Core.MessageHandlers;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Microsoft.Extensions.Logging;

namespace Benzene.Patterns.RealTimeRisk.ValuationService;

/// <summary>
/// The choreographed reaction from docs/patterns/reference-real-time-risk.md §2: a bar closed, so
/// re-mark every position exposed to that symbol.
/// </summary>
/// <remarks>
/// <para>An ordinary <c>[Message(...)]</c> handler - "an event is a topic, a reaction is a handler",
/// per docs/patterns/choreography.md. This service has no idea the Market-Data Aggregator exists; it
/// subscribes to a topic. Adding the next reaction (a limit monitor, a hedging trigger) is adding a
/// consumer, and changes nothing here or upstream.</para>
/// <para>Consumed with <c>Benzene.Kafka.Core</c>'s per-record <c>UseKafka</c>, not the streaming
/// binding: one closed bar is one independent reaction, not a window to aggregate. The streaming
/// binding exists for the <em>other</em> side of this hop, where ordering and rolling state across a
/// firehose are the whole problem.</para>
/// </remarks>
[Message(Topics.BarClosed)]
public class RevaluePositionsOnBarClosed : IMessageHandler<BarClosed>
{
    private readonly RiskReadModelsClient _readModels;
    private readonly PositionValuationStore _store;
    private readonly ILogger<RevaluePositionsOnBarClosed> _logger;

    public RevaluePositionsOnBarClosed(
        RiskReadModelsClient readModels,
        PositionValuationStore store,
        ILogger<RevaluePositionsOnBarClosed> logger)
    {
        _readModels = readModels;
        _store = store;
        _logger = logger;
    }

    public async Task HandleAsync(BarClosed message)
    {
        var exposure = await _readModels.GetExposureAsync(message.Symbol);

        var valuations = exposure.Books
            .Select(book => Value(book, message.Close))
            .ToList();

        _store.Record(message, valuations);

        _logger.LogInformation(
            "Revalued {BookCount} book(s) exposed to {Symbol} at {Close} (window ending {WindowEnd:HH:mm:ss})",
            valuations.Count, message.Symbol, message.Close, message.WindowEnd);
    }

    /// <summary>
    /// Marks one book's position to the bar's close.
    /// </summary>
    /// <remarks>
    /// <strong>Mark-to-market notional, not P&amp;L, and the difference is not pedantry.</strong> An
    /// unrealized-P&amp;L figure is <c>(mark - weighted average entry cost) x open quantity</c>, and
    /// nothing upstream tracks that entry cost: <c>PositionView</c> carries <c>NetQuantity</c> and
    /// <c>RealizedCash</c> only, because the ledger projection folds trades into a net position
    /// without keeping the open lots. Deriving a cost basis from <c>RealizedCash / NetQuantity</c>
    /// would be wrong the moment a book has both bought and sold - which is every real book - so the
    /// honest thing to compute is what the inputs actually support: the position's market value, and
    /// the realized cash already banked, reported side by side. See
    /// <see cref="PositionValuation"/>'s remarks; proper cost-basis tracking is a named follow-up in
    /// real-time-risk/README.md, not something to fake here.
    /// </remarks>
    private static PositionValuation Value(BookPositionView book, decimal close)
    {
        var marketValue = book.NetQuantity * close;
        return new PositionValuation
        {
            Book = book.Book,
            NetQuantity = book.NetQuantity,
            MarketValue = marketValue,
            RealizedCash = book.RealizedCash,
            TotalValue = marketValue + book.RealizedCash
        };
    }
}
