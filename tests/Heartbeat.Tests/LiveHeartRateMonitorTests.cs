using Heartbeat.Api.Live;
using Heartbeat.Shared;
using Microsoft.Extensions.Options;

namespace Heartbeat.Tests;

public sealed class LiveHeartRateMonitorTests
{
    private static LiveHeartRateMonitor Create(int seconds = 60)
        => new(new HealthEvaluator(), Options.Create(new LiveHeartRateOptions { CriticalDurationSeconds = seconds }));

    [Theory]
    [InlineData(39, HealthStatus.Critical)]
    [InlineData(40, HealthStatus.Watch)]
    [InlineData(50, HealthStatus.Normal)]
    [InlineData(100, HealthStatus.Normal)]
    [InlineData(101, HealthStatus.Watch)]
    [InlineData(121, HealthStatus.Critical)]
    public void Uses_the_heartRate_catalog_bands(int bpm, HealthStatus expected)
    {
        var result = Create().Observe(Guid.NewGuid(), new HeartRateReading(bpm, DateTimeOffset.UtcNow, "test"));

        Assert.Equal(expected, result.Event.Status);
        Assert.Single(result.Event.BandIds);
    }

    [Fact]
    public void An_isolated_critical_spike_does_not_raise_an_alert()
    {
        var monitor = Create();
        var watchId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        var spike = monitor.Observe(watchId, new HeartRateReading(130, start, "test"));
        var recovered = monitor.Observe(watchId, new HeartRateReading(80, start.AddSeconds(30), "test"));
        var laterSpike = monitor.Observe(watchId, new HeartRateReading(130, start.AddSeconds(70), "test"));

        Assert.False(spike.ShouldCreateAlert);
        Assert.False(recovered.ShouldCreateAlert);
        Assert.False(laterSpike.ShouldCreateAlert);
    }

    [Fact]
    public void A_sustained_episode_raises_once_and_recovery_allows_a_new_episode()
    {
        var monitor = Create(seconds: 10);
        var watchId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.False(monitor.Observe(watchId, new HeartRateReading(130, start, "test")).ShouldCreateAlert);
        Assert.False(monitor.Observe(watchId, new HeartRateReading(130, start.AddSeconds(9), "test")).ShouldCreateAlert);
        Assert.True(monitor.Observe(watchId, new HeartRateReading(130, start.AddSeconds(10), "test")).ShouldCreateAlert);
        Assert.False(monitor.Observe(watchId, new HeartRateReading(140, start.AddSeconds(20), "test")).ShouldCreateAlert);

        monitor.Observe(watchId, new HeartRateReading(80, start.AddSeconds(21), "test"));
        Assert.False(monitor.Observe(watchId, new HeartRateReading(35, start.AddSeconds(30), "test")).ShouldCreateAlert);
        Assert.True(monitor.Observe(watchId, new HeartRateReading(35, start.AddSeconds(40), "test")).ShouldCreateAlert);
    }
}
