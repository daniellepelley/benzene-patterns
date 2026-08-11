using Benzene.Patterns.RealTimeRisk.MarketDataAggregator;
using Xunit;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator.Tests;

/// <summary>
/// The watermark that makes an in-memory OHLC fold replay-safe under the streaming binding's
/// at-least-once retry policy.
/// </summary>
public class StreamReplayGuardTest
{
    [Fact]
    public void AnUnseenStreamAlwaysApplies()
    {
        var guard = new StreamReplayGuard();

        Assert.True(guard.ShouldApply("ticks [0]", 0));
        Assert.True(guard.ShouldApply("ticks [0]", 12345));
    }

    [Fact]
    public void ARecordAtOrBelowTheWatermarkIsAReplay()
    {
        var guard = new StreamReplayGuard();
        guard.MarkApplied("ticks [0]", 7);

        Assert.False(guard.ShouldApply("ticks [0]", 6));
        Assert.False(guard.ShouldApply("ticks [0]", 7));
        Assert.True(guard.ShouldApply("ticks [0]", 8));
    }

    [Fact]
    public void TheWatermarkNeverRewinds()
    {
        var guard = new StreamReplayGuard();
        guard.MarkApplied("ticks [0]", 7);
        guard.MarkApplied("ticks [0]", 3);

        Assert.False(guard.ShouldApply("ticks [0]", 5));
    }

    [Fact]
    public void PartitionsAreIndependent()
    {
        var guard = new StreamReplayGuard();
        guard.MarkApplied("ticks [0]", 100);

        Assert.True(guard.ShouldApply("ticks [1]", 0));
    }
}
