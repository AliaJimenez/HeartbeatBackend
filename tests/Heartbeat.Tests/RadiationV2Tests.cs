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

/// <summary>HB-BE-02 (#100): RadiationSummary v2 on the crew and Mission Control endpoints, and the radiation alerts.</summary>
public class RadiationV2Tests : IDisposable
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
    private readonly Guid _stationAstronautId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public RadiationV2Tests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var moon = new Mission { Id = Guid.NewGuid(), Name = "Moon", Environment = "lunarBase", StartsAt = Now.AddDays(-30) };
        var station = new Mission { Id = Guid.NewGuid(), Name = "Station", Environment = "station", StartsAt = Now.AddDays(-30) };
        var role = new Role { Id = Guid.NewGuid(), Name = "Surgeon" };
        db.Missions.AddRange(moon, station);
        db.Roles.Add(role);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "j.okafor", Email = "j@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id, IsActive = true });
        db.Astronauts.AddRange(
            new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana Rivera", MissionId = moon.Id, UpdatedAt = Now },
            new Astronaut { Id = _stationAstronautId, UserName = "ben", DisplayName = "Ben Ortiz", MissionId = station.Id, UpdatedAt = Now });
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

    private static RadiationDoseDto Dose(decimal msv, RadiationType type = RadiationType.Gcr, int minutesAgo = 5, Guid? id = null)
        => new(id ?? Guid.NewGuid(), Now.AddMinutes(-minutesAgo), null, msv, null, type);

    private static async Task<SyncResponse> Sync(HttpClient http, params RadiationDoseDto[] doses)
    {
        var request = new SyncRequest("dev", null, "2026.11.14-1", null, [], [], doses);
        var res = await http.PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SyncResponse>(Json))!;
    }

    private Task<RadiationSummaryDto> CrewSummary(Guid? id = null)
        => Crew(id).GetFromJsonAsync<RadiationSummaryDto>("/api/crew/v1/radiation", Json)!;

    private Task<RadiationSummaryDto> McSummary(Guid? id = null)
        => Mc(Permissions.VitalsView).GetFromJsonAsync<RadiationSummaryDto>($"/api/mc/v1/crew/{id ?? _astronautId}/radiation", Json)!;

    private Task<Paged<AlertDto>> Alerts(string state = "all")
        => Mc(Permissions.AlertsView).GetFromJsonAsync<Paged<AlertDto>>($"/api/mc/v1/alerts?state={state}", Json)!;

    [Fact]
    public async Task Empty_summary_is_normal_gcr_with_the_rate_of_the_mission_environment()
    {
        var moon = await CrewSummary();
        var station = await CrewSummary(_stationAstronautId);

        Assert.Equal((0m, 0, RadiationType.Gcr, HealthStatus.Normal), (moon.TotalMsv, moon.Entries, moon.Type, moon.Status));
        Assert.Equal(RadiationDoses.GcrRateMsvPerDayMoon, moon.Rate);
        Assert.Equal(RadiationDoses.GcrRateMsvPerDayStation, station.Rate);
    }

    [Fact]
    public async Task Summary_v2_reports_percent_of_the_career_limit_and_the_dominant_type_on_both_apps()
    {
        await Sync(Crew(), Dose(150), Dose(30, RadiationType.Trapped), Dose(60, RadiationType.Gcr));

        foreach (var summary in new[] { await CrewSummary(), await McSummary() })
        {
            Assert.Equal(240m, summary.TotalMsv);
            Assert.Equal(3, summary.Entries);
            Assert.Equal(40m, summary.PercentOfLimit);          // 240 / 600
            Assert.Equal(RadiationType.Gcr, summary.Type);      // 210 of the 240 mSv
            Assert.Equal(HealthStatus.Normal, summary.Status);
            Assert.Equal(0m, summary.SpeDoseMsv);
            Assert.Contains(summary.Doses, d => d.Type == RadiationType.Trapped);
        }
    }

    [Theory]
    [InlineData(299, HealthStatus.Normal)]
    [InlineData(300, HealthStatus.Watch)]
    [InlineData(500, HealthStatus.Watch)]
    [InlineData(600, HealthStatus.Critical)]
    public async Task Status_follows_the_career_limit(decimal totalMsv, HealthStatus expected)
    {
        var half = totalMsv / 2;
        await Sync(Crew(), Dose(half), Dose(totalMsv - half));

        Assert.Equal(expected, (await CrewSummary()).Status);
    }

    [Fact]
    public async Task Spe_event_is_judged_against_the_250_msv_per_event_limit_and_raises_an_alert()
    {
        var speId = Guid.NewGuid();
        await Sync(Crew(), Dose(100, RadiationType.Spe, id: speId));

        var summary = await McSummary();
        Assert.Equal(RadiationType.Spe, summary.Type);
        Assert.Equal(100m, summary.SpeDoseMsv);
        Assert.Equal(40m, summary.PercentOfSpeLimit);           // 100 / 250

        var alert = Assert.Single((await Alerts()).Items);
        Assert.Equal(AlertKind.Radiation, alert.Kind);
        Assert.Equal(speId, alert.RadiationDoseId);
        Assert.Null(alert.CheckUpId);
        Assert.Equal(_astronautId, alert.AstronautId);
        Assert.Equal("Ana Rivera", alert.AstronautName);
        Assert.Contains("simulated", alert.Message);
        Assert.Contains("40", alert.Message);
    }

    [Fact]
    public async Task A_single_spe_over_the_per_event_limit_makes_the_status_critical()
    {
        await Sync(Crew(), Dose(260, RadiationType.Spe));

        var summary = await CrewSummary();
        Assert.Equal(HealthStatus.Critical, summary.Status);    // 260 mSv total is only 43% of the career limit
        Assert.True(summary.PercentOfSpeLimit >= 100m);
        Assert.True(summary.PercentOfLimit < 50m);
    }

    [Fact]
    public async Task Reaching_the_career_limit_raises_one_alert_visible_in_the_portal()
    {
        var crew = Crew();
        await Sync(crew, Dose(400), Dose(150, minutesAgo: 4));
        Assert.Empty((await Alerts()).Items);                   // 550 mSv: watch, not critical

        var crossing = Dose(60, minutesAgo: 3);
        await Sync(crew, crossing);                              // 610 mSv
        await Sync(crew, Dose(20, minutesAgo: 2));               // still critical: no second alert

        var alert = Assert.Single((await Alerts()).Items);
        Assert.Equal(AlertKind.Radiation, alert.Kind);
        Assert.Equal(crossing.Id, alert.RadiationDoseId);
        Assert.Null(alert.CheckUpId);
        Assert.Contains("career limit", alert.Message);
    }

    [Fact]
    public async Task Normal_and_watch_doses_raise_no_alert()
    {
        await Sync(Crew(), Dose(200), Dose(150, RadiationType.Trapped, minutesAgo: 4));

        Assert.Empty((await Alerts()).Items);
    }

    [Fact]
    public async Task Retrying_a_sync_does_not_duplicate_the_alert()
    {
        var crew = Crew();
        var spe = Dose(80, RadiationType.Spe);
        await Sync(crew, spe);
        var retry = await Sync(crew, spe);

        Assert.Contains(spe.Id, retry.Accepted);
        Assert.Single((await Alerts()).Items);
        Assert.Equal(80m, retry.RadiationTotalMsv);
    }

    [Fact]
    public async Task Radiation_alerts_can_be_acknowledged_and_reach_the_astronaut_on_the_next_sync()
    {
        var spe = Dose(90, RadiationType.Spe);
        await Sync(Crew(), spe);
        var alert = Assert.Single((await Alerts()).Items);

        var surgeon = Mc(Permissions.AlertsView, Permissions.AlertsAcknowledge);
        var ack = await surgeon.PostAsJsonAsync($"/api/mc/v1/alerts/{alert.Id}/acknowledge", new AcknowledgeAlertRequest("Shelter in the storm cellar."), Json);
        Assert.Equal(HttpStatusCode.OK, ack.StatusCode);
        var acked = (await ack.Content.ReadFromJsonAsync<AlertDto>(Json))!;
        Assert.Equal(AlertKind.Radiation, acked.Kind);
        Assert.Equal("Dr. Okafor", acked.AcknowledgedBy);

        var next = await Sync(Crew());
        var inbound = Assert.Single(next.Inbound.AcknowledgedAlerts);
        Assert.Null(inbound.CheckUpId);
        Assert.Equal(spe.Id, inbound.RadiationDoseId);
        Assert.Equal("Dr. Okafor", inbound.By);
    }

    [Fact]
    public async Task Radiation_alerts_belong_to_the_astronaut_who_recorded_the_dose()
    {
        await Sync(Crew(_stationAstronautId), Dose(70, RadiationType.Spe));

        var item = Assert.Single((await Alerts()).Items);
        Assert.Equal(_stationAstronautId, item.AstronautId);
        Assert.Empty((await Alerts("acknowledged")).Items);
    }

    [Fact]
    public async Task Summary_endpoints_keep_their_permissions()
    {
        var path = $"/api/mc/v1/crew/{_astronautId}/radiation";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/crew/v1/radiation")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.AlertsView).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew().GetAsync(path)).StatusCode);
    }

    [Fact]
    public void Build_picks_the_dominant_type_by_dose_not_by_count_and_uses_the_environment_rate()
    {
        var doses = new[] { Dose(1), Dose(1), Dose(1), Dose(200, RadiationType.Spe) };

        var summary = Api.Crew.RadiationSummaries.Build(doses, "marsBase");

        Assert.Equal(RadiationType.Spe, summary.Type);
        Assert.Equal(RadiationDoses.GcrRateMsvPerDayMars, summary.Rate);
        Assert.Equal(203m, summary.TotalMsv);
    }

    [Theory]
    [InlineData("marsBase", "station", "marsBase")]      // environment profile set (HB-BE-01)
    [InlineData("station", "lunarBase", "lunarBase")]    // only the legacy field set
    [InlineData(null, null, "station")]
    [InlineData("station", "station", "station")]
    public void Environment_comes_from_the_profile_or_the_legacy_field(string? profile, string? legacy, string expected)
        => Assert.Equal(expected, Api.Crew.RadiationSummaries.EnvironmentOf(profile, legacy));
}
