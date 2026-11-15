using System.Net;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Live;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public sealed class LiveHeartRateAlertTests : WatchApiTestBase
{
    [Fact]
    public async Task Isolated_critical_spikes_do_not_create_alerts()
    {
        var key = await SeedWatchAsync(AnaId);
        var start = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 130, start)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 80, start.AddSeconds(30))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 130, start.AddSeconds(90))).StatusCode);

        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Empty(await db.Alerts.Where(a => a.AstronautId == AnaId).ToListAsync());
    }

    [Fact]
    public async Task A_sustained_critical_episode_creates_exactly_one_alert()
    {
        var key = await SeedWatchAsync(AnaId);
        var start = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        foreach (var seconds in new[] { 0, 30, 59, 60, 61, 90 })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 130, start.AddSeconds(seconds))).StatusCode);
        }

        using var scope = Api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var alert = await db.Alerts.SingleAsync(a => a.AstronautId == AnaId);
        Assert.Equal("HeartRate", alert.Indicators);
        Assert.Contains("130 bpm", alert.Message);
    }

    [Fact]
    public async Task A_post_emits_HeartRateStatus_with_the_catalog_status()
    {
        var key = await SeedWatchAsync(AnaId);
        var received = new TaskCompletionSource<HeartRateStatusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/heartrate", options =>
            {
                options.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(Token(AuthConstants.CrewAudience, AnaId));
            })
            .Build();
        connection.On<HeartRateStatusEvent>("HeartRateStatus", value => received.TrySetResult(value));
        await connection.StartAsync();

        for (var attempt = 0; attempt < 20 && !received.Task.IsCompleted; attempt++)
        {
            await PostReadingAsync(key, 121, DateTimeOffset.UtcNow.AddMilliseconds(attempt));
            await Task.Delay(100);
        }

        var status = await received.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(121, status.Bpm);
        Assert.Equal(HealthStatus.Critical, status.Status);
        Assert.Equal(["hr-critical-high"], status.BandIds);
    }
}
