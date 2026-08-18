using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Benzene.Abstractions.MessageHandlers;
using Benzene.Abstractions.Results;
using Benzene.Core.MessageHandlers;
using Benzene.Grpc;
using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Results;

namespace Benzene.Patterns.RealTimeRisk.PricingService.Handlers;

/// <summary>
/// Bidirectional session: the caller adjusts its watch list while the stream is open, and receives
/// ticks for whatever it is currently watching.
/// </summary>
/// <remarks>
/// <para>
/// The reference doc's headline shape for this service, and the one that earns the transport: a
/// blotter whose watch list changes while it is open cannot be served by a request/response call or
/// by a fixed subscription without tearing the connection down and rebuilding it.
/// </para>
/// <para>
/// The handler signature is <c>IAsyncEnumerable&lt;WatchRequest&gt;</c> in,
/// <c>IAsyncEnumerable&lt;PriceTick&gt;</c> out. Both halves are genuinely concurrent - the caller may
/// send a watch at any moment, including while a tick round is being written - so the two are joined
/// through a channel rather than by interleaving them in one loop, which could only read a request
/// between ticks.
/// </para>
/// <para>
/// <b>A watch is answered immediately</b> with a snapshot for that symbol, before the next scheduled
/// round. That is what a desk expects when it adds an instrument to a screen, and it also means the
/// number of ticks a session produces is a function of what the caller asked for rather than of how
/// long it happened to stay connected - so this is testable without racing a timer.
/// </para>
/// </remarks>
[GrpcMethod("/pricing.Pricing/PriceStream")]
[Message(Topics.PriceStream)]
public class PriceStreamHandler : IMessageHandler<IAsyncEnumerable<WatchRequest>, IAsyncEnumerable<PriceTick>>
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    private readonly IGrpcServerCallAccessor _call;

    public PriceStreamHandler(IGrpcServerCallAccessor call)
    {
        _call = call;
    }

    public Task<IBenzeneResult<IAsyncEnumerable<PriceTick>>> HandleAsync(IAsyncEnumerable<WatchRequest> request)
    {
        return BenzeneResult.Ok(Session(request, _call.CancellationToken)).AsTask();
    }

    private static async IAsyncEnumerable<PriceTick> Session(
        IAsyncEnumerable<WatchRequest> requests,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Symbol -> next sequence number for that symbol in this session, so a consumer can spot a
        // gap. Concurrent because the reader mutates the set while the ticker walks it.
        var watched = new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        var ticks = Channel.CreateUnbounded<PriceTick>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var reader = ReadWatchRequestsAsync(requests, watched, ticks.Writer, session);
        var ticker = TickAsync(watched, ticks.Writer, session.Token);

        try
        {
            await foreach (var tick in ticks.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return tick;
            }
        }
        finally
        {
            // The caller may abandon the enumeration at any point (disconnect, deadline, or simply
            // breaking out); without this the ticker would keep running against a channel nobody
            // reads. Cancelling here rather than only on the linked token covers that case too.
            session.Cancel();
            await Task.WhenAll(reader, ticker).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies the caller's watch/unwatch messages, answering each new watch with an immediate tick.
    /// Completes the channel when the caller half-closes, which ends the session.
    /// </summary>
    /// <remarks>
    /// Ending on half-close is a deliberate simplification for this demo: a production feed would keep
    /// streaming the established watch list until the deadline, because a desk that has finished
    /// choosing instruments has not finished watching them. Ending here keeps the session's lifetime
    /// tied to something a caller controls explicitly, which is what makes it scriptable.
    /// </remarks>
    private static async Task ReadWatchRequestsAsync(
        IAsyncEnumerable<WatchRequest> requests,
        ConcurrentDictionary<string, long> watched,
        ChannelWriter<PriceTick> writer,
        CancellationTokenSource session)
    {
        try
        {
            await foreach (var watch in requests.WithCancellation(session.Token).ConfigureAwait(false))
            {
                if (watch.Unwatch)
                {
                    watched.TryRemove(watch.Symbol, out _);
                    continue;
                }

                // An unknown symbol does not fail the whole session - one bad entry in a watch list
                // should not disconnect a desk from the instruments it got right. It is simply not
                // watched, and the absence of ticks for it is the answer. GetPrice is where a caller
                // asks a question that can be answered NotFound.
                if (!PriceFeed.IsKnown(watch.Symbol) || !watched.TryAdd(watch.Symbol, 1))
                {
                    continue;
                }

                await writer.WriteAsync(PriceFeed.Quote(watch.Symbol, 0, DateTimeOffset.UtcNow), session.Token)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Deadline, disconnect, or the reader walking away. Not an error.
        }
        finally
        {
            writer.TryComplete();
            session.Cancel();
        }
    }

    /// <summary>Emits a round of ticks for the current watch list on every interval.</summary>
    private static async Task TickAsync(
        ConcurrentDictionary<string, long> watched,
        ChannelWriter<PriceTick> writer,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TickInterval, cancellationToken).ConfigureAwait(false);

                foreach (var symbol in watched.Keys)
                {
                    // A symbol unwatched between taking the key snapshot and pricing it should not
                    // produce one last tick, so the sequence bump doubles as the membership check.
                    if (!watched.TryGetValue(symbol, out var sequence)
                        || !watched.TryUpdate(symbol, sequence + 1, sequence))
                    {
                        continue;
                    }

                    await writer.WriteAsync(PriceFeed.Quote(symbol, sequence, DateTimeOffset.UtcNow), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The session ended. Expected.
        }
    }
}
