using System.Net;
using System.Text;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Live;
using Heartbeat.Api.Salud;
using Heartbeat.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Heartbeat.Tests;

public sealed class WatchSyncImportTests : IAsyncDisposable
{
    private sealed class SingleClient(WatchSyncClient client) : IWatchSyncClients
    {
        public WatchSyncClient For(string? astronautUserName) => client;
    }

    private sealed class Backend : HttpMessageHandler
    {
        public bool Unavailable { get; set; }
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Unavailable)
            {
                throw new HttpRequestException("offline");
            }

            var json = request.RequestUri!.AbsolutePath.EndsWith("/sleep", StringComparison.Ordinal)
                ? """
                  [{"id":41,"start_at":"2026-11-14T22:00:00Z","end_at":"2026-11-15T06:00:00Z",
                    "tz_offset_min":0,"duration_min":480,"source":"Galaxy Watch","stage_minutes":null}]
                  """
                : """
                  [{"id":52,"measured_at":"2026-11-15T08:00:00Z","tz_offset_min":0,
                    "source":"Galaxy Watch","systolic":190,"diastolic":130}]
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppDbContext _db;
    private readonly Backend _backend = new();
    private readonly Guid _astronautId = Guid.NewGuid();

    public WatchSyncImportTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = DateTimeOffset.UtcNow };
        _db.Missions.Add(mission);
        _db.Astronauts.Add(new Astronaut
        {
            Id = _astronautId,
            UserName = "ana",
            DisplayName = "Ana",
            MissionId = mission.Id,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _db.WatchDevices.Add(new WatchDevice
        {
            Id = Guid.NewGuid(),
            AstronautId = _astronautId,
            DeviceId = "galaxy-watch-5",
            KeyHash = new string('a', 64),
            CreatedAt = DateTimeOffset.UtcNow,
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Imports_sleep_and_blood_pressure_once_and_raises_a_critical_alert()
    {
        var importer = CreateImporter();
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        await importer.ImportAllAsync(now, CancellationToken.None);
        await importer.ImportAllAsync(now, CancellationToken.None);

        var readings = await _db.Readings.OrderBy(r => r.Indicator).ToListAsync();
        Assert.Equal(2, readings.Count);
        Assert.All(readings, reading =>
        {
            Assert.Equal(ReadingSource.Wearable, reading.Source);
            Assert.Equal("galaxy-watch-5", reading.DeviceId);
            Assert.NotNull(reading.ExternalId);
        });
        Assert.Contains(readings, r => r.Indicator == IndicatorType.Sleep && r.Value == 8m);
        Assert.Contains(readings, r => r.Indicator == IndicatorType.BloodPressure && r.Value == 190m && r.SecondaryValue == 130m);
        Assert.Single(await _db.Alerts.ToListAsync());
    }

    [Fact]
    public async Task An_unavailable_WatchSync_does_not_fail_the_cycle_and_the_next_one_retries()
    {
        var importer = CreateImporter();
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        _backend.Unavailable = true;

        await importer.ImportAllAsync(now, CancellationToken.None);
        Assert.Empty(await _db.Readings.ToListAsync());
        var callsAfterFailure = _backend.Calls;

        _backend.Unavailable = false;
        await importer.ImportAllAsync(now, CancellationToken.None);

        Assert.True(_backend.Calls > callsAfterFailure);
        Assert.Equal(2, await _db.Readings.CountAsync());
    }

    private WatchSyncImportService CreateImporter()
    {
        var http = new HttpClient(_backend) { BaseAddress = new Uri("http://watchsync.test/api/v1/") };
        var client = new WatchSyncClient(http, "ana@example.com", "pw");
        var options = Options.Create(new WatchSyncOptions { ImportLookbackDays = 3 });
        var writer = new WearableReadingWriter(_db, new HealthEvaluator());
        return new WatchSyncImportService(
            _db,
            new SingleClient(client),
            writer,
            options,
            NullLogger<WatchSyncImportService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }
}
