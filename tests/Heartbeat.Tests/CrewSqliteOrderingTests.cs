using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

// SQLite cannot ORDER BY a DateTimeOffset; POST /sync and GET /checkups used to answer 500 because of it.
public class CrewSqliteOrderingTests : IClassFixture<CrewSqliteOrderingTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
            => builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(_dbPath); } catch (IOException) { }
        }
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private readonly Factory _factory;
    private readonly Guid _astronautId = Guid.NewGuid();
    private readonly Guid _otherAstronautId = Guid.NewGuid();
    private readonly List<Guid> _checkUpIdsOldestFirst = [];

    public CrewSqliteOrderingTests(Factory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = T0 };
        db.Missions.Add(mission);
        db.Astronauts.Add(new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = T0 });
        db.Astronauts.Add(new Astronaut { Id = _otherAstronautId, UserName = "kenji", DisplayName = "Kenji", MissionId = mission.Id, UpdatedAt = T0 });

        for (var day = 0; day < 5; day++)
        {
            var id = Guid.NewGuid();
            _checkUpIdsOldestFirst.Add(id);
            db.CheckUps.Add(CheckUp(id, _astronautId, day));
        }

        db.CheckUps.Add(CheckUp(Guid.NewGuid(), _otherAstronautId, 2));

        // Created out of order on purpose: the response must come back oldest first.
        foreach (var hour in new[] { 3, 1, 2 })
        {
            db.Messages.Add(new Message
            {
                Id = Guid.NewGuid(), AstronautId = _astronautId, Direction = MessageDirection.ToAstronaut, Kind = MessageKind.Message,
                Subject = $"m{hour}", Body = "b", CreatedAt = T0.AddHours(hour),
            });
        }

        db.SaveChanges();
    }

    private static CheckUp CheckUp(Guid id, Guid astronautId, int day) => new()
    {
        Id = id, AstronautId = astronautId, RecordedAt = T0.AddDays(day), ReceivedAt = T0.AddDays(day), OverallStatus = HealthStatus.Normal,
        RangesVersion = "x",
        Readings = { new Reading { Id = Guid.NewGuid(), Indicator = IndicatorType.Mood, Value = 4, Status = HealthStatus.Normal, BandIds = "mood-normal" } },
    };

    private HttpClient Client()
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(_astronautId, "Ana", AuthConstants.CrewAudience));
        return client;
    }

    [Fact]
    public async Task History_is_newest_first_and_only_mine()
    {
        var page = (await Client().GetFromJsonAsync<Paged<CheckUpDto>>("/api/crew/v1/checkups", Json))!;

        Assert.Equal(5, page.Total);
        Assert.Equal(Enumerable.Reverse(_checkUpIdsOldestFirst), page.Items.Select(c => c.Id));
        Assert.All(page.Items, c => Assert.Single(c.Readings));
    }

    [Fact]
    public async Task History_pages_keep_the_newest_first_order()
    {
        var client = Client();
        var first = (await client.GetFromJsonAsync<Paged<CheckUpDto>>("/api/crew/v1/checkups?page=1&pageSize=2", Json))!;
        var second = (await client.GetFromJsonAsync<Paged<CheckUpDto>>("/api/crew/v1/checkups?page=2&pageSize=2", Json))!;
        var third = (await client.GetFromJsonAsync<Paged<CheckUpDto>>("/api/crew/v1/checkups?page=3&pageSize=2", Json))!;

        var newestFirst = Enumerable.Reverse(_checkUpIdsOldestFirst).ToList();
        Assert.Equal(newestFirst.Take(2), first.Items.Select(c => c.Id));
        Assert.Equal(newestFirst.Skip(2).Take(2), second.Items.Select(c => c.Id));
        Assert.Equal(newestFirst.Skip(4), third.Items.Select(c => c.Id));
        Assert.All(new[] { first, second, third }, p => Assert.Equal(5, p.Total));
    }

    [Fact]
    public async Task History_filters_by_date_range()
    {
        var response = await Client().GetAsync(
            $"/api/crew/v1/checkups?from={Uri.EscapeDataString(T0.AddDays(1).ToString("O"))}&to={Uri.EscapeDataString(T0.AddDays(3).ToString("O"))}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var page = (await response.Content.ReadFromJsonAsync<Paged<CheckUpDto>>(Json))!;

        Assert.Equal([_checkUpIdsOldestFirst[3], _checkUpIdsOldestFirst[2], _checkUpIdsOldestFirst[1]], page.Items.Select(c => c.Id));
    }

    [Fact]
    public async Task Sync_returns_inbound_messages_oldest_first()
    {
        var request = new SyncRequest("device-1", null, "x", null, [], []);
        var response = await Client().PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = (await response.Content.ReadFromJsonAsync<SyncResponse>(Json))!;

        Assert.Equal(["m1", "m2", "m3"], body.Inbound.Messages.Select(m => m.Subject));
    }

    [Fact]
    public async Task A_later_sync_with_last_pulled_at_works()
    {
        var request = new SyncRequest("device-1", T0.AddHours(1).AddMinutes(30), "x", null, [], []);
        var response = await Client().PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var body = (await response.Content.ReadFromJsonAsync<SyncResponse>(Json))!;

        // Messages not yet delivered (ReceivedAt null) are always sent, whatever the cursor says.
        Assert.Equal(["m1", "m2", "m3"], body.Inbound.Messages.Select(m => m.Subject));
    }
}
