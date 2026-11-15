using System.Net;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Live;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace Heartbeat.Tests;

/// <summary>WR-TESTS (#154): cada POST de un reloj emite el evento <c>HeartRate</c> a los clientes conectados del astronauta.</summary>
public class HeartRateHubTests : WatchApiTestBase
{
    private sealed class Received
    {
        private readonly List<HeartRateReading> _items = [];

        public void Add(HeartRateReading r)
        {
            lock (_items)
            {
                _items.Add(r);
            }
        }

        public void Clear()
        {
            lock (_items)
            {
                _items.Clear();
            }
        }

        public List<HeartRateReading> Snapshot()
        {
            lock (_items)
            {
                return _items.ToList();
            }
        }

        public int Count => Snapshot().Count;
    }

    private HubConnection Connect(string token, Received received, string query = "")
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://localhost/hubs/heartrate{query}", options =>
            {
                options.HttpMessageHandlerFactory = _ => Api.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        connection.On<HeartRateReading>("HeartRate", received.Add);
        return connection;
    }

    private HubConnection ConnectCrew(Guid astronautId, Received received) => Connect(Token(AuthConstants.CrewAudience, astronautId), received);

    private static async Task<bool> WaitForAsync(Func<bool> condition, int milliseconds = 3000)
    {
        for (var waited = 0; waited < milliseconds; waited += 50)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50);
        }

        return condition();
    }

    /// <summary>
    /// StartAsync vuelve al terminar el handshake y el cliente entra al grupo justo después: se envían lecturas de prueba
    /// hasta que llega una, y luego se vacía lo recibido para que la prueba parta de cero.
    /// </summary>
    private async Task WarmUpAsync(string key, params Received[] clients)
    {
        for (var attempt = 0; attempt < 40 && clients.Any(c => c.Count == 0); attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 40)).StatusCode);
            await Task.Delay(100);
        }

        Assert.All(clients, c => Assert.True(c.Count > 0, "the client never joined its group"));
        await Task.Delay(200);
        foreach (var client in clients)
        {
            client.Clear();
        }
    }

    [Fact]
    public async Task A_post_emits_HeartRate_to_a_connected_client()
    {
        var key = await SeedWatchAsync(AnaId);
        var received = new Received();
        await using var connection = ConnectCrew(AnaId, received);
        await connection.StartAsync();
        await WarmUpAsync(key, received);
        var measuredAt = new DateTimeOffset(2026, 11, 15, 10, 15, 30, TimeSpan.Zero);

        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 88, measuredAt, "galaxy-watch-5")).StatusCode);

        Assert.True(await WaitForAsync(() => received.Count == 1));
        var reading = received.Snapshot().Single();
        Assert.Equal(88, reading.Bpm);
        Assert.Equal(measuredAt, reading.MeasuredAt);
        Assert.Equal("galaxy-watch-5", reading.Source);
    }

    [Fact]
    public async Task Each_post_emits_exactly_one_event_in_order()
    {
        var key = await SeedWatchAsync(AnaId);
        var received = new Received();
        await using var connection = ConnectCrew(AnaId, received);
        await connection.StartAsync();
        await WarmUpAsync(key, received);

        foreach (var bpm in new[] { 100, 101, 102, 103, 104 })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, bpm)).StatusCode);
        }

        Assert.True(await WaitForAsync(() => received.Count >= 5));
        await Task.Delay(300);
        Assert.Equal([100, 101, 102, 103, 104], received.Snapshot().Select(r => r.Bpm));
    }

    [Fact]
    public async Task Every_connected_client_of_the_astronaut_receives_the_reading()
    {
        var key = await SeedWatchAsync(AnaId);
        Received crew = new(), portal = new();
        await using var crewConnection = ConnectCrew(AnaId, crew);
        await using var portalConnection = Connect(
            Token(AuthConstants.McAudience, Guid.NewGuid(), Heartbeat.Shared.Permissions.VitalsView), portal, $"?astronautId={AnaId}");
        await crewConnection.StartAsync();
        await portalConnection.StartAsync();
        await WarmUpAsync(key, crew, portal);

        await PostReadingAsync(key, 91);

        Assert.True(await WaitForAsync(() => crew.Count == 1 && portal.Count == 1));
        Assert.Equal(91, crew.Snapshot().Single().Bpm);
        Assert.Equal(91, portal.Snapshot().Single().Bpm);
    }

    [Fact]
    public async Task A_rejected_post_emits_nothing()
    {
        var key = await SeedWatchAsync(AnaId);
        var received = new Received();
        await using var connection = ConnectCrew(AnaId, received);
        await connection.StartAsync();
        await WarmUpAsync(key, received);

        Assert.Equal(HttpStatusCode.BadRequest, (await PostReadingAsync(key, 300)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await PostReadingAsync(key, 10)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync("not-a-registered-key", 80)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostReadingAsync(null, 80)).StatusCode);

        await Task.Delay(500);
        Assert.Equal(0, received.Count);
    }

    [Fact]
    public async Task A_client_that_disconnected_receives_nothing_and_posting_still_works()
    {
        var key = await SeedWatchAsync(AnaId);
        var received = new Received();
        await using var connection = ConnectCrew(AnaId, received);
        await connection.StartAsync();
        await WarmUpAsync(key, received);

        await connection.StopAsync();

        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 77)).StatusCode);
        await Task.Delay(400);
        Assert.Equal(0, received.Count);
        Assert.Equal(77, (await RecentAsync(AnaId)).Last().Bpm);
    }

    [Fact]
    public async Task A_reading_is_emitted_to_the_astronauts_group_only()
    {
        var anaKey = await SeedWatchAsync(AnaId);
        var benKey = await SeedWatchAsync(BenId);
        Received ana = new(), ben = new();
        await using var anaConnection = ConnectCrew(AnaId, ana);
        await using var benConnection = ConnectCrew(BenId, ben);
        await anaConnection.StartAsync();
        await benConnection.StartAsync();
        await WarmUpAsync(anaKey, ana);
        await WarmUpAsync(benKey, ben);

        await PostReadingAsync(anaKey, 65);
        await PostReadingAsync(benKey, 130);

        Assert.True(await WaitForAsync(() => ana.Count == 1 && ben.Count == 1));
        await Task.Delay(300);
        Assert.Equal([65], ana.Snapshot().Select(r => r.Bpm));
        Assert.Equal([130], ben.Snapshot().Select(r => r.Bpm));
    }
}
