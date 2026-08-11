namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator;

/// <summary>
/// A per-stream "highest sequence already folded in" watermark, so a replayed record is skipped
/// rather than double-counted.
/// </summary>
/// <remarks>
/// <para>docs/patterns/streaming-processing.md's checklist requires that "aggregation is
/// idempotent/replayable, because delivery is at-least-once with resume-from-sequence". Its worked
/// example gets that from an <c>UpsertAsync(symbol, minute, bar)</c> against a store: replaying a
/// batch recomputes the same item. <see cref="OhlcBarAggregator"/> has no store to upsert into - it
/// accumulates volume and tick counts in memory - so replaying a record would silently inflate the
/// bar. This watermark is what makes the fold replay-safe instead.</para>
/// <para><c>Benzene.Kafka.Streaming</c>'s retry policy makes replay a real, routine path, not a
/// theoretical one: a batch whose pipeline throws is seeked back <em>per partition</em> to one past
/// its checkpoint and re-consumed, so every record the failed attempt had already processed but not
/// checkpointed comes round again. Advancing this watermark in lockstep with the checkpointer means
/// the second attempt folds in exactly the records the first one didn't.</para>
/// <para>The residual, stated plainly: a record whose own <c>bar:closed</c> publish is what threw is
/// not marked, so its tick is folded twice into the newly-opened window on the retry (an over-count
/// bounded by one tick), and the bar that failed to publish is gone with the in-memory state that
/// held it. Both are consequences of keeping rolling state in memory rather than a store - see
/// <see cref="OhlcBarAggregator"/>'s remarks - not something a bigger watermark would fix.</para>
/// </remarks>
public sealed class StreamReplayGuard
{
    private readonly Dictionary<string, long> _appliedThrough = new(StringComparer.Ordinal);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="sequence"/> on <paramref name="streamKey"/> has not
    /// been seen before (i.e. the caller should process it), <c>false</c> if it is a replay.
    /// </summary>
    public bool ShouldApply(string streamKey, long sequence) =>
        !_appliedThrough.TryGetValue(streamKey, out var applied) || sequence > applied;

    /// <summary>Records that everything up to and including <paramref name="sequence"/> has been folded in.</summary>
    public void MarkApplied(string streamKey, long sequence)
    {
        if (!_appliedThrough.TryGetValue(streamKey, out var applied) || sequence > applied)
        {
            _appliedThrough[streamKey] = sequence;
        }
    }
}
