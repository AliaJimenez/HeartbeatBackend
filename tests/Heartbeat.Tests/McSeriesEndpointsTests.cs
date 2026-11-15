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
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

public class McSeriesEndpointsTests : IClassFixture<McSeriesEndpointsTests.Factory>
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
    private readonly Guid _emptyAstronautId = Guid.NewGuid();

    public McSeriesEndpointsTests(Factory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = T0 };
        db.Missions.Add(mission);
        db.Astronauts.AddRange(
            new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = T0 },
            new Astronaut { Id = _emptyAstronautId, UserName = "kenji", DisplayName = "Kenji", MissionId = mission.Id, UpdatedAt = T0 });

        var wrong = Guid.NewGuid();
        CheckUp C(Guid id, int day, Guid? supersedes = null) => new()
        {
            Id = id, AstronautId = _astronautId, RecordedAt = T0.AddDays(day), ReceivedAt = T0.AddDays(day).AddMinutes(5),
            OverallStatus = HealthStatus.Normal, RangesVersion = "2026.11.14-1", SupersedesId = supersedes,
        };
        Reading R(Guid c, IndicatorType i, decimal v, decimal? v2, HealthStatus s, ReadingSource source = ReadingSource.Manual) =>
            new() { Id = Guid.NewGuid(), CheckUpId = c, Indicator = i, Value = v, SecondaryValue = v2, Status = s, BandIds = "x", Source = source };

        var day0 = Guid.NewGuid();
        var day2 = Guid.NewGuid();
        var day3 = Guid.NewGuid();
        var day5 = Guid.NewGuid();
        db.CheckUps.AddRange(C(day3, 3), C(day0, 0), C(wrong, 1), C(day2, 2, supersedes: wrong), C(day5, 5));
        db.Readings.AddRange(
            R(day0, IndicatorType.HeartRate, 60, null, HealthStatus.Normal),
            R(wrong, IndicatorType.HeartRate, 200, null, HealthStatus.Critical),
            R(day2, IndicatorType.HeartRate, 70, null, HealthStatus.Normal),
            R(day3, IndicatorType.HeartRate, 130, null, HealthStatus.Critical, ReadingSource.Wearable),
            R(day5, IndicatorType.HeartRate, 75, null, HealthStatus.Normal),
            R(day3, IndicatorType.Sleep, 7, null, HealthStatus.Normal),
            R(day3, IndicatorType.BloodPressure, 185, 95, HealthStatus.Critical));
        db.SaveChanges();
    }

    private HttpClient Client(params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(Guid.NewGuid(), "Dr", AuthConstants.McAudience, permissions: permissions));
        return client;
    }

    private Task<SeriesDto> Series(string query, Guid? id = null)
        => Client(Permissions.VitalsHistory).GetFromJsonAsync<SeriesDto>($"/api/mc/v1/crew/{id ?? _astronautId}/series{query}", Json)!;

    [Fact]
    public async Task Requires_vitals_history()
    {
        var path = $"/api/mc/v1/crew/{_astronautId}/series?indicator=heartRate";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Permissions.VitalsView).GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Points_are_oldest_first_and_exclude_replaced_checkups()
    {
        var series = await Series("?indicator=heartRate");

        Assert.Equal(IndicatorType.HeartRate, series.Indicator);
        Assert.Equal("bpm", series.Unit);
        Assert.Equal([60m, 70m, 130m, 75m], series.Points.Select(p => p.Value)); // 200 belongs to the replaced check-up
        Assert.Equal([T0, T0.AddDays(2), T0.AddDays(3), T0.AddDays(5)], series.Points.Select(p => p.T));
        Assert.Equal(HealthStatus.Critical, series.Points[2].Status);
        Assert.Equal(ReadingSource.Wearable, series.Points[2].Source);
    }

    [Theory]
    [InlineData("manual", new[] { 60, 70, 75 })]
    [InlineData("wearable", new[] { 130 })]
    [InlineData("all", new[] { 60, 70, 130, 75 })]
    public async Task Source_filters_points_and_all_keeps_both(string source, int[] expected)
    {
        var series = await Series($"?indicator=heartRate&source={source}");

        Assert.Equal(expected.Select(value => (decimal)value), series.Points.Select(p => p.Value));
    }

    [Fact]
    public async Task Points_only_include_the_requested_indicator()
    {
        var series = await Series("?indicator=sleep");

        var point = Assert.Single(series.Points);
        Assert.Equal(7m, point.Value);
        Assert.Equal("hours/day", series.Unit);
    }

    [Fact]
    public async Task From_and_to_limit_the_points()
    {
        var from = Uri.EscapeDataString(T0.AddDays(1).ToString("o"));
        var to = Uri.EscapeDataString(T0.AddDays(3).ToString("o"));

        var series = await Series($"?indicator=heartRate&from={from}&to={to}");

        Assert.Equal([70m, 130m], series.Points.Select(p => p.Value));
    }

    [Fact]
    public async Task Bands_come_from_the_reference_ranges_for_shading()
    {
        var series = await Series("?indicator=heartRate");

        Assert.Equal(5, series.Bands.Count);
        Assert.Equal(new SeriesBandDto(HealthStatus.Critical, null, 40), series.Bands[0]);
        Assert.Equal(new SeriesBandDto(HealthStatus.Normal, 50, 101), series.Bands[2]);
        Assert.Equal(new SeriesBandDto(HealthStatus.Critical, 121, null), series.Bands[^1]);
    }

    [Fact]
    public async Task Blood_pressure_has_both_values_and_systolic_bands()
    {
        var series = await Series("?indicator=bloodPressure");

        var point = Assert.Single(series.Points);
        Assert.Equal((185m, 95m), (point.Value, point.SecondaryValue));
        Assert.Equal("mmHg", series.Unit);
        Assert.Equal(new SeriesBandDto(HealthStatus.Normal, 90, 130), series.Bands.Single(b => b.Status == HealthStatus.Normal));
    }

    [Fact]
    public async Task Indicator_is_case_insensitive_and_serialized_as_camel_case()
    {
        var body = await Client(Permissions.VitalsHistory)
            .GetStringAsync($"/api/mc/v1/crew/{_astronautId}/series?indicator=HEARTRATE");

        Assert.Contains("\"indicator\":\"heartRate\"", body);
        Assert.Contains("\"t\":\"2026-11-10T09:00:00", body);
        Assert.Contains("\"status\":\"normal\"", body);
    }

    [Fact]
    public async Task Astronaut_without_checkups_gets_empty_points()
    {
        var series = await Series("?indicator=heartRate", _emptyAstronautId);

        Assert.Empty(series.Points);
        Assert.NotEmpty(series.Bands);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?indicator=")]
    [InlineData("?indicator=cholesterol")]
    [InlineData("?indicator=99")]
    public async Task Missing_or_unknown_indicator_is_400(string query)
    {
        var res = await Client(Permissions.VitalsHistory).GetAsync($"/api/mc/v1/crew/{_astronautId}/series{query}");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task From_after_to_is_400()
    {
        var from = Uri.EscapeDataString(T0.AddDays(3).ToString("o"));
        var to = Uri.EscapeDataString(T0.ToString("o"));

        var res = await Client(Permissions.VitalsHistory)
            .GetAsync($"/api/mc/v1/crew/{_astronautId}/series?indicator=heartRate&from={from}&to={to}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_source_is_400()
    {
        var res = await Client(Permissions.VitalsHistory)
            .GetAsync($"/api/mc/v1/crew/{_astronautId}/series?indicator=heartRate&source=implant");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_astronaut_is_404()
    {
        var res = await Client(Permissions.VitalsHistory)
            .GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/series?indicator=heartRate");

        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
