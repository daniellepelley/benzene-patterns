using Benzene.Patterns.RealTimeRisk.Contracts;
using Benzene.Patterns.RealTimeRisk.MarketDataAggregator;
using Xunit;

namespace Benzene.Patterns.RealTimeRisk.MarketDataAggregator.Tests;

/// <summary>
/// The OHLC roll-up is the one piece of real logic in the Market-Data Aggregator, and it is pure -
/// no Kafka, no clock, no DI - so it is tested directly rather than through a broker.
/// </summary>
public class OhlcBarAggregatorTest
{
    private static readonly DateTimeOffset Minute0 = new(2026, 8, 11, 14, 30, 0, TimeSpan.Zero);
    private static readonly TimeSpan OneMinute = TimeSpan.FromMinutes(1);

    private static MarketTick Tick(string symbol, decimal price, decimal size, DateTimeOffset at) =>
        new() { Symbol = symbol, Price = price, Size = size, Timestamp = at };

    [Fact]
    public void ATickInsideTheOpenWindowClosesNothing()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);

        Assert.Null(aggregator.Add(Tick("AAPL", 150m, 10, Minute0)));
        Assert.Null(aggregator.Add(Tick("AAPL", 151m, 10, Minute0.AddSeconds(30))));
    }

    [Fact]
    public void TheFirstTickOfANewMinuteClosesThePreviousBar()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);

        aggregator.Add(Tick("AAPL", 150m, 10, Minute0.AddSeconds(1)));
        aggregator.Add(Tick("AAPL", 153m, 20, Minute0.AddSeconds(20)));
        aggregator.Add(Tick("AAPL", 149m, 30, Minute0.AddSeconds(40)));
        aggregator.Add(Tick("AAPL", 152m, 40, Minute0.AddSeconds(55)));

        var bar = aggregator.Add(Tick("AAPL", 160m, 5, Minute0.AddMinutes(1)));

        Assert.NotNull(bar);
        Assert.Equal("AAPL", bar!.Symbol);
        Assert.Equal(150m, bar.Open);
        Assert.Equal(153m, bar.High);
        Assert.Equal(149m, bar.Low);
        Assert.Equal(152m, bar.Close);
        Assert.Equal(100m, bar.Volume);
        Assert.Equal(4, bar.TickCount);
        Assert.Equal(Minute0, bar.WindowStart);
        Assert.Equal(Minute0.AddMinutes(1), bar.WindowEnd);
    }

    [Fact]
    public void TheTickThatClosedABarOpensTheNextOne()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0));

        aggregator.Add(Tick("AAPL", 160m, 7, Minute0.AddMinutes(1)));
        var second = aggregator.Add(Tick("AAPL", 170m, 3, Minute0.AddMinutes(2)));

        // The minute-1 bar is built only from the tick that closed minute 0 - the minute-2 tick that
        // closed it belongs to the next window, not this one.
        Assert.NotNull(second);
        Assert.Equal(Minute0.AddMinutes(1), second!.WindowStart);
        Assert.Equal(160m, second.Open);
        Assert.Equal(160m, second.Close);
        Assert.Equal(7m, second.Volume);
        Assert.Equal(1, second.TickCount);
    }

    [Fact]
    public void SymbolsRollIndependently()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0));
        aggregator.Add(Tick("MSFT", 400m, 10, Minute0));

        var appleBar = aggregator.Add(Tick("AAPL", 155m, 10, Minute0.AddMinutes(1)));

        Assert.NotNull(appleBar);
        Assert.Equal("AAPL", appleBar!.Symbol);

        // MSFT's window has not rolled, so its bar is still open.
        Assert.Empty(aggregator.CloseCompleted(Minute0.AddSeconds(59)));
    }

    [Fact]
    public void CloseCompletedFlushesAWindowThatHasEndedInWallClockTerms()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0.AddSeconds(10)));

        Assert.Empty(aggregator.CloseCompleted(Minute0.AddSeconds(59)));

        var closed = aggregator.CloseCompleted(Minute0.AddMinutes(1));

        var bar = Assert.Single(closed);
        Assert.Equal(150m, bar.Close);
        Assert.Equal(Minute0, bar.WindowStart);
    }

    [Fact]
    public void CloseCompletedFlushesEveryDueSymbolOldestWindowFirst()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("MSFT", 400m, 10, Minute0.AddMinutes(1)));
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0));

        var closed = aggregator.CloseCompleted(Minute0.AddMinutes(5));

        Assert.Equal(new[] { "AAPL", "MSFT" }, closed.Select(bar => bar.Symbol).ToArray());
        Assert.Equal(new[] { Minute0, Minute0.AddMinutes(1) }, closed.Select(bar => bar.WindowStart).ToArray());
    }

    [Fact]
    public void ATickForAnAlreadyClosedWindowIsDroppedAsLate()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0));
        aggregator.Add(Tick("AAPL", 160m, 10, Minute0.AddMinutes(1)));

        Assert.Null(aggregator.Add(Tick("AAPL", 999m, 10, Minute0.AddSeconds(30))));
        Assert.Equal(1, aggregator.LateTickCount);

        // The late tick must not have polluted the currently open bar.
        var bar = aggregator.CloseCompleted(Minute0.AddMinutes(2)).Single();
        Assert.Equal(160m, bar.High);
        Assert.Equal(10m, bar.Volume);
    }

    [Fact]
    public void ATickForAWindowAlreadyFlushedByCloseCompletedIsDroppedRatherThanReopeningIt()
    {
        var aggregator = new OhlcBarAggregator(OneMinute);
        aggregator.Add(Tick("AAPL", 150m, 10, Minute0));
        Assert.Single(aggregator.CloseCompleted(Minute0.AddMinutes(1)));

        Assert.Null(aggregator.Add(Tick("AAPL", 151m, 10, Minute0.AddSeconds(45))));
        Assert.Equal(1, aggregator.LateTickCount);
        Assert.Empty(aggregator.CloseCompleted(Minute0.AddMinutes(5)));
    }

    [Theory]
    [InlineData("2026-08-11T14:30:00Z", 60, "2026-08-11T14:30:00Z")]
    [InlineData("2026-08-11T14:30:59.999Z", 60, "2026-08-11T14:30:00Z")]
    [InlineData("2026-08-11T14:31:00Z", 60, "2026-08-11T14:31:00Z")]
    [InlineData("2026-08-11T14:30:37Z", 10, "2026-08-11T14:30:30Z")]
    public void WindowsAreFlooredFromTheUnixEpochInUtc(string timestamp, int intervalSeconds, string expected)
    {
        var actual = OhlcBarAggregator.WindowStartFor(
            DateTimeOffset.Parse(timestamp, null, System.Globalization.DateTimeStyles.RoundtripKind),
            TimeSpan.FromSeconds(intervalSeconds));

        Assert.Equal(
            DateTimeOffset.Parse(expected, null, System.Globalization.DateTimeStyles.RoundtripKind),
            actual);
    }

    [Fact]
    public void AnOffsetTimestampIsBucketedByItsUtcInstantNotItsLocalWallClock()
    {
        var utc = new DateTimeOffset(2026, 8, 11, 14, 30, 30, TimeSpan.Zero);
        var sameInstantInAnotherZone = utc.ToOffset(TimeSpan.FromHours(5.5));

        Assert.Equal(
            OhlcBarAggregator.WindowStartFor(utc, OneMinute),
            OhlcBarAggregator.WindowStartFor(sameInstantInAnotherZone, OneMinute));
    }

    [Fact]
    public void ANonPositiveBarIntervalIsRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OhlcBarAggregator(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new OhlcBarAggregator(TimeSpan.FromSeconds(-1)));
    }
}
