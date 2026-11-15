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
using Reading = Heartbeat.Shared.EnvironmentReading;

namespace Heartbeat.Tests;

/// <summary>HB-BE-03 (#101): habitat readings through POST /sync, GET /mc/crew/{id}/environment, critical bands raise alerts.</summary>
public class EnvironmentApiTests : IDisposable
{
    private sealed class Factory : WebApplicationFactory<Program>
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

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Factory _factory = new();
    private readonly Guid _astronautId = Guid.NewGuid();
    private readonly Guid _otherAstronautId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public EnvironmentApiTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = Now.AddDays(-30) };
        var role = new Role { Id = Guid.NewGuid(), Name = "Surgeon" };
        db.Missions.Add(mission);
        db.Roles.Add(role);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "j.okafor", Email = "j@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id, IsActive = true });
        db.Astronauts.AddRange(
            new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana Rivera", MissionId = mission.Id, UpdatedAt = Now },
            new Astronaut { Id = _otherAstronautId, UserName = "ben", DisplayName = "Ben Ortiz", MissionId = mission.Id, UpdatedAt = Now });
        db.SaveChanges();
    }

    private HttpClient Authorized(string audience, Guid userId, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId, "Someone", audience, permissions: permissions));
        return client;
    }

    private HttpClient Crew(Guid? id = null) => Authorized(AuthConstants.CrewAudience, id ?? _astronautId);

    private HttpClient Mc(params string[] permissions) => Authorized(AuthConstants.McAudience, _surgeonId, permissions);

    private static Reading Env(string indicator, decimal value, int minutesAgo = 5, Guid? id = null, string? note = null)
        => new(id ?? Guid.NewGuid(), Now.AddMinutes(-minutesAgo), null, indicator, value, Note: note);

    private static async Task<SyncResponse> Sync(HttpClient http, params Reading[] readings)
    {
        var request = new SyncRequest("dev", null, "2026.11.14-1", null, [], [], null, readings);
        var res = await http.PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SyncResponse>(Json))!;
    }

    private Task<EnvironmentSummaryDto> Environment(string query = "", Guid? id = null)
        => Mc(Permissions.VitalsView).GetFromJsonAsync<EnvironmentSummaryDto>($"/api/mc/v1/crew/{id ?? _astronautId}/environment{query}", Json)!;

    private Task<Paged<AlertDto>> Alerts(string state = "all")
        => Mc(Permissions.AlertsView).GetFromJsonAsync<Paged<AlertDto>>($"/api/mc/v1/alerts?state={state}", Json)!;

    [Fact]
    public async Task Without_readings_the_environment_is_empty_and_normal()
    {
        var env = await Environment();

        Assert.Equal(HealthStatus.Normal, env.Status);
        Assert.Empty(env.Latest);
        Assert.Empty(env.History.Items);
        Assert.True(env.Simulated);
    }

    [Fact]
    public async Task Readings_pushed_by_sync_are_evaluated_with_the_catalog_bands()
    {
        var sync = await Sync(Crew(),
            Env("co2", 2.1m), Env("o2", 165m), Env("pressure", 101m), Env("temperature", 22m),
            Env("humidity", 45m), Env("light", 450m), Env("noise", 48m));
        Assert.Equal(7, sync.Accepted.Count);
        Assert.Empty(sync.Rejected);

        var env = await Environment();
        Assert.Equal(7, env.Latest.Count);
        Assert.All(env.Latest, r => Assert.Equal(HealthStatus.Normal, r.Status));
        Assert.Equal(HealthStatus.Normal, env.Status);

        var co2 = env.Latest.Single(r => r.IndicatorId == "co2");
        Assert.Equal("mmHg", co2.Unit);
        Assert.Equal(CatalogStatus.Heuristic, co2.CatalogStatus);
        Assert.StartsWith("https://", co2.Source.Url.ToString());
        Assert.True(co2.Simulated);
        Assert.Empty((await Alerts()).Items);
    }

    [Theory]
    [InlineData("co2", 2.99, HealthStatus.Normal)]
    [InlineData("co2", 3, HealthStatus.Watch)]
    [InlineData("co2", 4.99, HealthStatus.Watch)]
    [InlineData("co2", 5, HealthStatus.Critical)]
    [InlineData("o2", 139.9, HealthStatus.Critical)]
    [InlineData("o2", 140, HealthStatus.Watch)]
    [InlineData("o2", 170, HealthStatus.Normal)]
    [InlineData("temperature", 17.9, HealthStatus.Critical)]
    [InlineData("noise", 70, HealthStatus.Critical)]
    [InlineData("light", 99, HealthStatus.Critical)]
    public async Task Each_reading_lands_in_the_band_the_catalog_defines(string indicator, double value, HealthStatus expected)
    {
        await Sync(Crew(), Env(indicator, (decimal)value));

        var env = await Environment();
        Assert.Equal(expected, Assert.Single(env.Latest).Status);
        Assert.Equal(expected, env.Status);
    }

    [Fact]
    public async Task Overall_status_is_the_worst_of_the_latest_reading_of_each_indicator()
    {
        await Sync(Crew(), Env("co2", 6m, minutesAgo: 30), Env("co2", 2m, minutesAgo: 5), Env("noise", 60m));

        var env = await Environment();
        Assert.Equal(HealthStatus.Watch, env.Status);                   // CO2 recovered; noise is watch
        Assert.Equal(2m, env.Latest.Single(r => r.IndicatorId == "co2").Value);
        Assert.Equal(3, env.History.Total);
    }

    [Fact]
    public async Task A_critical_reading_creates_an_alert_that_appears_in_the_portal()
    {
        await Sync(Crew(), Env("co2", 5.8m));

        var alert = Assert.Single((await Alerts()).Items);
        Assert.Equal(AlertKind.Environment, alert.Kind);
        Assert.Equal(_astronautId, alert.AstronautId);
        Assert.Equal("Ana Rivera", alert.AstronautName);
        Assert.Null(alert.CheckUpId);
        Assert.Contains("critical", alert.Message);
        Assert.Contains("simulated", alert.Message);
        Assert.Contains("5.8", alert.Message);
    }

    [Fact]
    public async Task Watch_readings_raise_no_alert()
    {
        await Sync(Crew(), Env("co2", 4m), Env("pressure", 92m));

        Assert.Empty((await Alerts()).Items);
    }

    [Fact]
    public async Task Retrying_a_sync_does_not_duplicate_readings_or_alerts()
    {
        var crew = Crew();
        var reading = Env("co2", 5.5m);
        await Sync(crew, reading);
        var retry = await Sync(crew, reading);

        Assert.Contains(reading.Id, retry.Accepted);
        Assert.Single((await Alerts()).Items);
        Assert.Equal(1, (await Environment()).History.Total);
    }

    [Fact]
    public async Task While_an_alert_for_an_indicator_is_open_new_critical_readings_do_not_pile_up()
    {
        var crew = Crew();
        await Sync(crew, Env("co2", 5.2m, minutesAgo: 20));
        await Sync(crew, Env("co2", 5.9m, minutesAgo: 10), Env("co2", 6.1m, minutesAgo: 5));
        await Sync(crew, Env("noise", 80m));      // a different indicator is a different alert

        var open = (await Alerts("open")).Items;
        Assert.Equal(2, open.Count);
        Assert.Equal(4, (await Environment()).History.Total);
    }

    [Fact]
    public async Task After_the_alert_is_acknowledged_a_new_critical_reading_raises_a_new_one()
    {
        await Sync(Crew(), Env("co2", 5.2m, minutesAgo: 20));
        var first = Assert.Single((await Alerts()).Items);

        var ack = await Mc(Permissions.AlertsAcknowledge).PostAsJsonAsync(
            $"/api/mc/v1/alerts/{first.Id}/acknowledge", new AcknowledgeAlertRequest("Scrubbers checked."), Json);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);

        var next = await Sync(Crew(), Env("co2", 5.7m));
        var ackBack = Assert.Single(next.Inbound.AcknowledgedAlerts);
        Assert.Equal("Dr. Okafor", ackBack.By);

        Assert.Equal(2, (await Alerts()).Items.Count);
        Assert.Single((await Alerts("open")).Items);
    }

    [Fact]
    public async Task Unknown_and_non_environment_indicators_are_rejected_but_the_rest_of_the_batch_goes_through()
    {
        var good = Env("humidity", 50m);
        var unknown = Env("flux-capacitor", 1m);
        var body = Env("stress", 5m);          // a catalog indicator, but not a habitat one
        var absurd = Env("co2", 1_000_000m);

        var sync = await Sync(Crew(), good, unknown, body, absurd);

        Assert.Equal([good.Id], sync.Accepted);
        Assert.Equal(3, sync.Rejected.Count);
        Assert.Contains(sync.Rejected, r => r.Id == unknown.Id && r.Reason.Contains("not an environment indicator"));
        Assert.Contains(sync.Rejected, r => r.Id == body.Id);
        Assert.Contains(sync.Rejected, r => r.Id == absurd.Id && r.Reason.Contains("out of range"));
        Assert.Equal(1, (await Environment()).History.Total);
    }

    [Fact]
    public async Task A_reading_id_used_by_another_astronaut_is_rejected()
    {
        var shared = Guid.NewGuid();
        await Sync(Crew(_otherAstronautId), Env("co2", 2m, id: shared));

        var sync = await Sync(Crew(), Env("co2", 2m, id: shared));

        Assert.Contains(sync.Rejected, r => r.Id == shared);
        Assert.Equal(0, (await Environment()).History.Total);
        Assert.Equal(1, (await Environment(id: _otherAstronautId)).History.Total);
    }

    [Fact]
    public async Task History_filters_by_indicator_and_date_and_pages_newest_first()
    {
        await Sync(Crew(),
            Env("co2", 2m, minutesAgo: 50), Env("co2", 2.5m, minutesAgo: 40), Env("co2", 3.5m, minutesAgo: 30),
            Env("noise", 50m, minutesAgo: 20));

        var co2 = await Environment("?indicator=co2&pageSize=2");
        Assert.Equal(3, co2.History.Total);
        Assert.Equal([3.5m, 2.5m], co2.History.Items.Select(r => r.Value));

        var page2 = await Environment("?indicator=co2&pageSize=2&page=2");
        Assert.Equal([2m], page2.History.Items.Select(r => r.Value));

        var from = Uri.EscapeDataString(Now.AddMinutes(-35).ToString("O"));
        var recent = await Environment($"?from={from}");
        Assert.Equal(2, recent.History.Total);
        Assert.Equal(2, recent.Latest.Count);     // the latest-per-indicator view ignores the history filters
    }

    [Theory]
    [InlineData("?indicator=stress")]
    [InlineData("?indicator=nope")]
    [InlineData("?page=0")]
    [InlineData("?pageSize=500")]
    public async Task Invalid_queries_are_a_400(string query)
    {
        var res = await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{_astronautId}/environment{query}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_astronaut_is_a_404_and_the_endpoint_requires_vitals_view()
    {
        var mc = Mc(Permissions.VitalsView);
        Assert.Equal(HttpStatusCode.NotFound, (await mc.GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/environment")).StatusCode);

        var path = $"/api/mc/v1/crew/{_astronautId}/environment";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.AlertsView).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Readings_of_one_astronaut_do_not_show_for_another()
    {
        await Sync(Crew(_otherAstronautId), Env("co2", 6m));

        Assert.Empty((await Environment()).Latest);
        Assert.Equal(_otherAstronautId, Assert.Single((await Alerts()).Items).AstronautId);
    }

    [Fact]
    public void Catalog_evaluation_uses_min_inclusive_max_exclusive_and_covers_the_environment_indicators()
    {
        var ids = Api.Crew.EnvironmentReadings.EnvironmentIndicators().Select(e => e.Id).OrderBy(i => i).ToList();
        Assert.Equal(["co2", "humidity", "light", "noise", "o2", "pressure", "temperature"], ids);

        var co2 = IndicatorsCatalog.Find("co2")!;
        Assert.Equal(HealthStatus.Normal, IndicatorsCatalog.Evaluate(co2, 2.999m).Status);
        Assert.Equal(HealthStatus.Watch, IndicatorsCatalog.Evaluate(co2, 3m).Status);
        Assert.Equal(HealthStatus.Critical, IndicatorsCatalog.Evaluate(co2, 5m).Status);
        Assert.Equal(HealthStatus.Critical, IndicatorsCatalog.Evaluate(co2, 50m).Status);
    }
}
