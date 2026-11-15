using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class ReadingSourceTests : IClassFixture<ReadingSourceTests.Factory>
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

    public ReadingSourceTests(Factory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = T0 };
        db.Missions.Add(mission);
        db.Astronauts.Add(new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = T0 });
        db.SaveChanges();
    }

    private HttpClient Client(string audience = AuthConstants.CrewAudience, Guid? userId = null, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId ?? _astronautId, "Ana", audience, permissions: permissions));
        return client;
    }

    private static ReadingDto Reading(IndicatorType indicator, decimal value, ReadingSource source = ReadingSource.Manual, string? deviceId = null)
        => new(Guid.NewGuid(), indicator, value, null, HealthStatus.Normal, [], source, deviceId);

    private static CheckUpDto CheckUp(Guid id, params ReadingDto[] readings)
        => new(id, T0, null, HealthStatus.Normal, "x", null, null, readings);

    private async Task<SyncResponse> SyncAsync(params CheckUpDto[] checkUps)
    {
        var request = new SyncRequest("device-1", null, "x", null, checkUps, []);
        var response = await Client().PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SyncResponse>(Json))!;
    }

    private async Task<List<Reading>> StoredReadingsAsync(Guid checkUpId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Readings.AsNoTracking().Where(r => r.CheckUpId == checkUpId).ToListAsync();
    }

    [Fact]
    public void A_reading_is_manual_without_a_device_by_default()
    {
        var entity = new Reading();
        Assert.Equal(ReadingSource.Manual, entity.Source);
        Assert.Null(entity.DeviceId);

        var dto = new ReadingDto(Guid.NewGuid(), IndicatorType.Mood, 4, null, HealthStatus.Normal, []);
        Assert.Equal(ReadingSource.Manual, dto.Source);
        Assert.Null(dto.DeviceId);
    }

    [Fact]
    public void Source_travels_as_a_camel_case_string()
    {
        var json = JsonSerializer.Serialize(Reading(IndicatorType.HeartRate, 70, ReadingSource.Wearable, "watch-1"), Json);

        Assert.Contains("\"source\":\"wearable\"", json);
        Assert.Contains("\"deviceId\":\"watch-1\"", json);
        Assert.Contains("\"source\":\"manual\"", JsonSerializer.Serialize(Reading(IndicatorType.HeartRate, 70), Json));
    }

    [Fact]
    public void A_payload_without_source_still_reads_as_manual()
    {
        const string old = """
            { "id": "0193b000-0000-7000-8000-000000000001", "indicator": "heartRate", "value": 88, "secondaryValue": null,
              "status": "normal", "bandIds": ["hr-normal"] }
            """;

        var dto = JsonSerializer.Deserialize<ReadingDto>(old, Json)!;

        Assert.Equal(ReadingSource.Manual, dto.Source);
        Assert.Null(dto.DeviceId);
    }

    [Fact]
    public async Task Sync_stores_the_source_and_device_of_each_reading()
    {
        var checkUpId = Guid.NewGuid();
        var response = await SyncAsync(CheckUp(
            checkUpId,
            Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, "  pixel-watch-3  "),
            Reading(IndicatorType.Sleep, 7)));

        Assert.Contains(checkUpId, response.Accepted);
        var stored = await StoredReadingsAsync(checkUpId);
        var heart = stored.Single(r => r.Indicator == IndicatorType.HeartRate);
        var sleep = stored.Single(r => r.Indicator == IndicatorType.Sleep);
        Assert.Equal((ReadingSource.Wearable, "pixel-watch-3"), (heart.Source, heart.DeviceId));
        Assert.Equal((ReadingSource.Manual, (string?)null), (sleep.Source, sleep.DeviceId));
    }

    [Fact]
    public async Task A_blank_device_id_is_stored_as_null()
    {
        var checkUpId = Guid.NewGuid();
        await SyncAsync(CheckUp(checkUpId, Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, "   ")));

        Assert.Null((await StoredReadingsAsync(checkUpId)).Single().DeviceId);
    }

    [Fact]
    public async Task Sync_rejects_a_check_up_whose_device_id_is_too_long()
    {
        var checkUpId = Guid.NewGuid();
        var response = await SyncAsync(CheckUp(
            checkUpId,
            Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, new string('d', Heartbeat.Api.Data.Entities.Reading.DeviceIdMaxLength + 1))));

        Assert.DoesNotContain(checkUpId, response.Accepted);
        Assert.Equal(checkUpId, Assert.Single(response.Rejected).Id);
        Assert.Empty(await StoredReadingsAsync(checkUpId));
    }

    [Fact]
    public async Task A_device_id_of_exactly_the_maximum_length_is_accepted()
    {
        var checkUpId = Guid.NewGuid();
        var response = await SyncAsync(CheckUp(
            checkUpId,
            Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, new string('d', Heartbeat.Api.Data.Entities.Reading.DeviceIdMaxLength))));

        Assert.Contains(checkUpId, response.Accepted);
    }

    [Fact]
    public async Task Wearable_readings_are_evaluated_like_manual_ones()
    {
        var checkUpId = Guid.NewGuid();
        await SyncAsync(CheckUp(checkUpId, Reading(IndicatorType.HeartRate, 190, ReadingSource.Wearable, "watch-1")));

        var heart = (await StoredReadingsAsync(checkUpId)).Single();
        Assert.Equal(HealthStatus.Critical, heart.Status);
        Assert.NotEmpty(heart.BandIds);
    }

    [Fact]
    public async Task Crew_history_returns_source_and_device()
    {
        var checkUpId = Guid.NewGuid();
        await SyncAsync(CheckUp(checkUpId, Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, "watch-1")));

        var page = (await Client().GetFromJsonAsync<Paged<CheckUpDto>>("/api/crew/v1/checkups", Json))!;

        var reading = page.Items.Single(c => c.Id == checkUpId).Readings.Single();
        Assert.Equal((ReadingSource.Wearable, "watch-1"), (reading.Source, reading.DeviceId));
    }

    [Fact]
    public async Task Mission_control_check_up_detail_and_history_return_source_and_device()
    {
        var checkUpId = Guid.NewGuid();
        await SyncAsync(CheckUp(
            checkUpId,
            Reading(IndicatorType.HeartRate, 72, ReadingSource.Wearable, "watch-1"),
            Reading(IndicatorType.Mood, 4)));

        var mc = Client(AuthConstants.McAudience, Guid.NewGuid(), Permissions.VitalsView, Permissions.VitalsHistory);
        var detail = (await mc.GetFromJsonAsync<CheckUpDetailDto>($"/api/mc/v1/crew/{_astronautId}/checkups/{checkUpId}", Json))!;
        var history = (await mc.GetFromJsonAsync<Paged<CheckUpDto>>($"/api/mc/v1/crew/{_astronautId}/checkups", Json))!;

        Assert.Equal(ReadingSource.Wearable, detail.Readings.Single(r => r.Indicator == IndicatorType.HeartRate).Source);
        Assert.Equal("watch-1", detail.Readings.Single(r => r.Indicator == IndicatorType.HeartRate).DeviceId);
        Assert.Equal(ReadingSource.Manual, detail.Readings.Single(r => r.Indicator == IndicatorType.Mood).Source);
        Assert.Contains(history.Items.Single(c => c.Id == checkUpId).Readings, r => r.Source == ReadingSource.Wearable);
    }

    [Fact]
    public async Task Existing_readings_without_a_source_column_value_read_as_manual()
    {
        var checkUpId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.CheckUps.Add(new CheckUp
            {
                Id = checkUpId, AstronautId = _astronautId, RecordedAt = T0, ReceivedAt = T0, OverallStatus = HealthStatus.Normal,
                RangesVersion = "x",
                Readings = { new Reading { Id = Guid.NewGuid(), Indicator = IndicatorType.Mood, Value = 4, Status = HealthStatus.Normal, BandIds = "mood-normal" } },
            });
            await db.SaveChangesAsync();
        }

        var reading = (await StoredReadingsAsync(checkUpId)).Single();
        Assert.Equal(ReadingSource.Manual, reading.Source);
        Assert.Null(reading.DeviceId);
    }

    [Fact]
    public async Task Syncing_the_same_check_up_twice_keeps_the_first_source()
    {
        var checkUpId = Guid.NewGuid();
        var readingId = Guid.NewGuid();
        var first = new ReadingDto(readingId, IndicatorType.HeartRate, 72, null, HealthStatus.Normal, [], ReadingSource.Wearable, "watch-1");
        await SyncAsync(CheckUp(checkUpId, first));
        var second = first with { Source = ReadingSource.Manual, DeviceId = null };

        var response = await SyncAsync(CheckUp(checkUpId, second));

        Assert.Contains(checkUpId, response.Accepted);
        var stored = (await StoredReadingsAsync(checkUpId)).Single();
        Assert.Equal((ReadingSource.Wearable, "watch-1"), (stored.Source, stored.DeviceId));
    }
}
