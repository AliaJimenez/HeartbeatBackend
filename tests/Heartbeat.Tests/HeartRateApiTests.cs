using System.Net;
using System.Net.Http.Json;
using System.Text;
using Heartbeat.Api.Live;

namespace Heartbeat.Tests;

/// <summary>WR-TESTS (#154): POST /api/heartrate, GET /latest y GET /recent (rango de bpm, 204, ventana de 1 a 600 s, búfer de 600).</summary>
public class HeartRateApiTests : WatchApiTestBase
{
    // ----- POST: rango de bpm -----

    [Theory]
    [InlineData(25, HttpStatusCode.OK)]
    [InlineData(72, HttpStatusCode.OK)]
    [InlineData(250, HttpStatusCode.OK)]
    [InlineData(24, HttpStatusCode.BadRequest)]
    [InlineData(251, HttpStatusCode.BadRequest)]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(-60, HttpStatusCode.BadRequest)]
    [InlineData(1000, HttpStatusCode.BadRequest)]
    public async Task Bpm_is_accepted_from_25_to_250_inclusive(int bpm, HttpStatusCode expected)
    {
        var key = await SeedWatchAsync(AnaId);

        var res = await PostReadingAsync(key, bpm);

        Assert.Equal(expected, res.StatusCode);
        if (expected == HttpStatusCode.BadRequest)
        {
            Assert.Contains("bpm fuera de rango", await res.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Rejected_readings_are_not_stored()
    {
        var key = await SeedWatchAsync(AnaId);

        await PostReadingAsync(key, 24);
        await PostReadingAsync(key, 251);

        Assert.Equal(HttpStatusCode.NoContent, (await Crew(AnaId).GetAsync("/api/heartrate/latest")).StatusCode);
        Assert.Empty(await RecentAsync(AnaId));
    }

    [Fact]
    public async Task A_malformed_body_is_400_and_stores_nothing()
    {
        var key = await SeedWatchAsync(AnaId);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/heartrate")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json"),
        };
        request.Headers.Add(WatchKeys.HeaderName, key);

        var res = await Api.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await RecentAsync(AnaId));
    }

    [Fact]
    public async Task A_reading_without_source_is_accepted()
    {
        var key = await SeedWatchAsync(AnaId);

        Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, 70, source: null)).StatusCode);

        Assert.Null((await RecentAsync(AnaId)).Single().Source);
    }

    // ----- GET /latest -----

    [Fact]
    public async Task Latest_is_204_without_readings()
    {
        await SeedWatchAsync(AnaId);

        var res = await Crew(AnaId).GetAsync("/api/heartrate/latest");

        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
        Assert.Empty(await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Latest_is_the_last_reading_received_with_the_watch_json_names()
    {
        var key = await SeedWatchAsync(AnaId);
        var measuredAt = new DateTimeOffset(2026, 11, 15, 9, 30, 0, TimeSpan.Zero);

        await PostReadingAsync(key, 70, measuredAt.AddSeconds(-1));
        await PostReadingAsync(key, 81, measuredAt, "galaxy-watch-5");

        var body = await Crew(AnaId).GetStringAsync("/api/heartrate/latest");
        Assert.Contains("\"bpm\":81", body);
        Assert.Contains("\"measuredAt\":\"2026-11-15T09:30:00", body);
        Assert.Contains("\"source\":\"galaxy-watch-5\"", body);
    }

    // ----- GET /recent: ventana de 1 a 600 segundos -----

    [Fact]
    public async Task Recent_is_an_empty_list_without_readings()
    {
        Assert.Empty(await RecentAsync(AnaId));
    }

    // Lecturas medidas hace 0,3 s (bpm 60), 30 s (61), 400 s (62), 590 s (63) y 700 s (64).
    private async Task SeedAgesAsync()
    {
        var key = await SeedWatchAsync(AnaId);
        var now = DateTimeOffset.UtcNow;
        foreach (var (bpm, age) in new[] { (64, 700.0), (63, 590.0), (62, 400.0), (61, 30.0), (60, 0.3) })
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(key, bpm, now.AddSeconds(-age))).StatusCode);
        }
    }

    [Theory]
    [InlineData("", new[] { 61, 60 })]                         // 300 s por defecto
    [InlineData("?seconds=300", new[] { 61, 60 })]
    [InlineData("?seconds=0", new[] { 60 })]                   // menos de 1 s se sube a 1 s
    [InlineData("?seconds=-5", new[] { 60 })]
    [InlineData("?seconds=1", new[] { 60 })]
    [InlineData("?seconds=60", new[] { 61, 60 })]
    [InlineData("?seconds=600", new[] { 63, 62, 61, 60 })]
    [InlineData("?seconds=100000", new[] { 63, 62, 61, 60 })]  // más de 600 s se baja a 600 s
    public async Task Recent_applies_the_window_clamped_between_1_and_600_seconds(string query, int[] expected)
    {
        await SeedAgesAsync();

        var readings = await RecentAsync(AnaId, query);

        Assert.Equal(expected, readings.Select(r => r.Bpm));
    }

    [Fact]
    public async Task Recent_filters_by_the_time_measured_not_the_time_received()
    {
        var key = await SeedWatchAsync(AnaId);

        await PostReadingAsync(key, 66, DateTimeOffset.UtcNow.AddHours(-2));

        Assert.Empty(await RecentAsync(AnaId));
        Assert.Equal(66, (await Crew(AnaId).GetFromJsonAsync<HeartRateReading>("/api/heartrate/latest", Json))!.Bpm);
    }

    [Fact]
    public async Task Recent_is_in_chronological_order_whatever_the_arrival_order()
    {
        var key = await SeedWatchAsync(AnaId);
        var now = DateTimeOffset.UtcNow;

        await PostReadingAsync(key, 90, now.AddSeconds(-10));
        await PostReadingAsync(key, 70, now.AddSeconds(-30));
        await PostReadingAsync(key, 80, now.AddSeconds(-20));

        Assert.Equal([70, 80, 90], (await RecentAsync(AnaId)).Select(r => r.Bpm));
    }

    // ----- Búfer de 600 lecturas -----

    [Fact]
    public async Task The_buffer_keeps_600_readings_and_drops_the_oldest()
    {
        var anaKey = await SeedWatchAsync(AnaId);
        var benKey = await SeedWatchAsync(BenId);
        var start = DateTimeOffset.UtcNow;

        for (var i = 0; i < 605; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await PostReadingAsync(anaKey, 25 + (i % 200), start.AddMilliseconds(i))).StatusCode);
        }

        await PostReadingAsync(benKey, 99);

        var ana = await RecentAsync(AnaId, "?seconds=600");
        Assert.Equal(HeartRateBuffer.Capacity, ana.Count);
        Assert.Equal(start.AddMilliseconds(5), ana[0].MeasuredAt);
        Assert.Equal(start.AddMilliseconds(604), ana[^1].MeasuredAt);
        Assert.Equal(25 + (604 % 200), (await Crew(AnaId).GetFromJsonAsync<HeartRateReading>("/api/heartrate/latest", Json))!.Bpm);

        // El tope es por astronauta: las 605 lecturas de Ana no tocan el búfer de Ben.
        Assert.Equal([99], (await RecentAsync(BenId)).Select(r => r.Bpm));
    }
}
