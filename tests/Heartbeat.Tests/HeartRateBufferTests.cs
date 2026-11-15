using Heartbeat.Api.Live;

namespace Heartbeat.Tests;

/// <summary>WR-TESTS (#154): el búfer en memoria de la FC en vivo (600 lecturas) y el almacén por astronauta.</summary>
public class HeartRateBufferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 8, 0, 0, TimeSpan.Zero);

    private static HeartRateReading At(int seconds, int bpm = 70) => new(bpm, T0.AddSeconds(seconds), "test");

    [Fact]
    public void A_new_buffer_has_no_latest_and_no_readings()
    {
        var buffer = new HeartRateBuffer();

        Assert.Null(buffer.Latest);
        Assert.Empty(buffer.Since(DateTimeOffset.MinValue));
    }

    [Fact]
    public void Latest_is_the_last_reading_added()
    {
        var buffer = new HeartRateBuffer();

        buffer.Add(At(0, 60));
        buffer.Add(At(1, 90));

        Assert.Equal(90, buffer.Latest!.Bpm);
    }

    [Fact]
    public void Latest_is_the_last_one_received_even_if_it_was_measured_earlier()
    {
        var buffer = new HeartRateBuffer();

        buffer.Add(At(10, 80));
        buffer.Add(At(5, 75));

        Assert.Equal(75, buffer.Latest!.Bpm);
    }

    [Fact]
    public void Adding_null_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new HeartRateBuffer().Add(null!));
    }

    [Fact]
    public void The_capacity_is_600_readings()
    {
        Assert.Equal(600, HeartRateBuffer.Capacity);
    }

    [Theory]
    [InlineData(599)]
    [InlineData(600)]
    public void Up_to_the_capacity_nothing_is_dropped(int count)
    {
        var buffer = new HeartRateBuffer();

        for (var i = 0; i < count; i++)
        {
            buffer.Add(At(i));
        }

        Assert.Equal(count, buffer.Since(DateTimeOffset.MinValue).Count);
    }

    [Fact]
    public void Past_the_capacity_the_oldest_readings_are_dropped()
    {
        var buffer = new HeartRateBuffer();

        for (var i = 0; i < 650; i++)
        {
            buffer.Add(At(i, 25 + (i % 200)));
        }

        var kept = buffer.Since(DateTimeOffset.MinValue);
        Assert.Equal(HeartRateBuffer.Capacity, kept.Count);
        Assert.Equal(T0.AddSeconds(50), kept[0].MeasuredAt);
        Assert.Equal(T0.AddSeconds(649), kept[^1].MeasuredAt);
        Assert.Equal(kept[^1], buffer.Latest);
    }

    [Fact]
    public void Since_keeps_the_readings_measured_at_or_after_the_limit()
    {
        var buffer = new HeartRateBuffer();
        foreach (var second in new[] { 0, 10, 20, 30 })
        {
            buffer.Add(At(second));
        }

        Assert.Equal([T0.AddSeconds(20), T0.AddSeconds(30)], buffer.Since(T0.AddSeconds(20)).Select(r => r.MeasuredAt));
        Assert.Empty(buffer.Since(T0.AddSeconds(31)));
    }

    [Fact]
    public void Since_returns_chronological_order_whatever_the_arrival_order()
    {
        var buffer = new HeartRateBuffer();
        foreach (var second in new[] { 30, 10, 20 })
        {
            buffer.Add(At(second, 60 + second));
        }

        Assert.Equal([70, 80, 90], buffer.Since(DateTimeOffset.MinValue).Select(r => r.Bpm));
    }

    [Fact]
    public void Since_returns_a_copy_that_later_additions_do_not_change()
    {
        var buffer = new HeartRateBuffer();
        buffer.Add(At(0));
        var snapshot = buffer.Since(DateTimeOffset.MinValue);

        buffer.Add(At(1));

        Assert.Single(snapshot);
    }

    [Fact]
    public void Concurrent_adds_never_exceed_the_capacity_or_throw()
    {
        var buffer = new HeartRateBuffer();

        Parallel.For(0, 4000, i =>
        {
            buffer.Add(At(i, 25 + (i % 200)));
            _ = buffer.Latest;
            _ = buffer.Since(T0);
        });

        Assert.Equal(HeartRateBuffer.Capacity, buffer.Since(DateTimeOffset.MinValue).Count);
        Assert.NotNull(buffer.Latest);
    }

    // ----- HeartRateStore: one buffer per astronaut -----

    [Fact]
    public void The_store_gives_each_astronaut_their_own_buffer()
    {
        var store = new HeartRateStore();
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();

        store.For(ana).Add(At(0, 60));

        Assert.Same(store.For(ana), store.For(ana));
        Assert.NotSame(store.For(ana), store.For(ben));
        Assert.Equal(60, store.Find(ana)!.Latest!.Bpm);
        Assert.Null(store.For(ben).Latest);
    }

    [Fact]
    public void The_store_finds_nothing_for_an_astronaut_without_readings()
    {
        Assert.Null(new HeartRateStore().Find(Guid.NewGuid()));
    }

    [Fact]
    public void The_capacity_applies_to_each_astronaut_separately()
    {
        var store = new HeartRateStore();
        var ana = Guid.NewGuid();
        var ben = Guid.NewGuid();

        for (var i = 0; i < 700; i++)
        {
            store.For(ana).Add(At(i));
        }

        store.For(ben).Add(At(0));

        Assert.Equal(HeartRateBuffer.Capacity, store.For(ana).Since(DateTimeOffset.MinValue).Count);
        Assert.Single(store.For(ben).Since(DateTimeOffset.MinValue));
    }
}
