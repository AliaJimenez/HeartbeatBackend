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
using EnvironmentReading = Heartbeat.Shared.EnvironmentReading;

namespace Heartbeat.Tests;

/// <summary>HB-BE-05 (#113): IOP and visual acuity through POST /sync, GET /mc/crew/{id}/vision with trend and CO2 correlation.</summary>
public class VisionApiTests : IDisposable
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

    private static readonly DateTimeOffset Today = DateTimeOffset.UtcNow.Date;

    private readonly Factory _factory = new();
    private readonly Guid _astronautId = Guid.NewGuid();
    private readonly Guid _otherAstronautId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public VisionApiTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = Today.AddDays(-30) };
        var role = new Role { Id = Guid.NewGuid(), Name = "Surgeon" };
        db.Missions.Add(mission);
        db.Roles.Add(role);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "j.okafor", Email = "j@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id, IsActive = true });
        db.Astronauts.AddRange(
            new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana Rivera", MissionId = mission.Id, UpdatedAt = Today },
            new Astronaut { Id = _otherAstronautId, UserName = "ben", DisplayName = "Ben Ortiz", MissionId = mission.Id, UpdatedAt = Today });
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

    // Daily readings: day -N at 08:00 UTC, so a series of N+1 values ends today.
    private static EnvironmentReading R(string indicator, decimal value, int daysAgo)
        => new(Guid.NewGuid(), Today.AddDays(-daysAgo).AddHours(8), null, indicator, value);

    private static async Task<SyncResponse> Sync(HttpClient http, params EnvironmentReading[] readings)
    {
        var request = new SyncRequest("dev", null, "2026.11.14-1", null, [], [], null, readings);
        var res = await http.PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SyncResponse>(Json))!;
    }

    private Task<VisionSummaryDto> Vision(string query = "", Guid? id = null)
        => Mc(Permissions.VitalsView).GetFromJsonAsync<VisionSummaryDto>($"/api/mc/v1/crew/{id ?? _astronautId}/vision{query}", Json)!;

    private static EnvironmentReading[] Series(string indicator, params decimal[] values)
        => values.Select((v, i) => R(indicator, v, values.Length - 1 - i)).ToArray();

    [Fact]
    public async Task Without_readings_the_vision_view_is_empty_and_normal()
    {
        var v = await Vision();

        Assert.Equal(HealthStatus.Normal, v.Status);
        Assert.Equal(0, v.Iop.Count);
        Assert.Equal(TrendDirection.Unknown, v.Iop.Direction);
        Assert.Null(v.IopVsCo2.R);
        Assert.True(v.Simulated);
        Assert.Equal(CatalogStatus.Heuristic, v.CatalogStatus);
    }

    [Fact]
    public async Task Iop_and_visual_acuity_enter_by_sync_and_come_back_with_trend_status_and_source()
    {
        var sync = await Sync(Crew(),
            [.. Series("iop", 15m, 16m, 17m, 18m, 19m), .. Series("visualAcuity", 0.1m, 0.1m, 0.2m, 0.2m, 0.3m)]);
        Assert.Equal(10, sync.Accepted.Count);
        Assert.Empty(sync.Rejected);

        var v = await Vision();

        Assert.Equal(TrendDirection.Rising, v.Iop.Direction);
        Assert.Equal(19m, v.Iop.Latest);
        Assert.Equal(HealthStatus.Normal, v.Iop.Status);
        Assert.Equal("mmHg", v.Iop.Unit);
        Assert.Equal(5, v.Iop.Readings.Count);
        Assert.Equal(TrendDirection.Rising, v.VisualAcuity.Direction);
        Assert.Equal(HealthStatus.Watch, v.VisualAcuity.Status);          // 0.3 logMAR is the start of the watch band
        Assert.Equal(HealthStatus.Watch, v.Status);
        Assert.StartsWith("https://", v.Source.Url.ToString());
        Assert.All(v.Iop.Readings, r => Assert.True(r.Simulated));
    }

    [Fact]
    public async Task Iop_that_follows_co2_in_the_same_period_is_correlated()
    {
        await Sync(Crew(), [.. Series("iop", 15m, 16m, 17m, 18m, 19m), .. Series("co2", 2m, 2.5m, 3m, 3.5m, 4m)]);

        var v = await Vision();

        Assert.Equal(1m, v.IopVsCo2.R);
        Assert.Equal(5, v.IopVsCo2.Pairs);
        Assert.Equal(CorrelationStrength.Strong, v.IopVsCo2.Strength);
        Assert.Null(v.VisualAcuityVsCo2.R);                              // no acuity readings
    }

    [Fact]
    public async Task The_period_filter_applies_to_vision_and_co2_alike()
    {
        // Days -9..-5: CO2 and IOP rise together. Days -4..0: IOP keeps rising but CO2 falls.
        await Sync(Crew(),
        [
            .. Series("iop", 12m, 13m, 14m, 15m, 16m, 17m, 18m, 19m, 20m, 21m),
            .. Series("co2", 1m, 2m, 3m, 4m, 5m, 5m, 4m, 3m, 2m, 1m),
        ]);

        var all = await Vision();
        var first = Uri.EscapeDataString(Today.AddDays(-9).ToString("O"));
        var mid = Uri.EscapeDataString(Today.AddDays(-5).AddHours(23).ToString("O"));
        var early = await Vision($"?from={first}&to={mid}");

        Assert.Equal(10, all.Iop.Count);
        Assert.Equal(5, early.Iop.Count);
        Assert.Equal(1m, early.IopVsCo2.R);                              // only the days that rose together
        Assert.True(all.IopVsCo2.R < early.IopVsCo2.R);
        Assert.NotNull(early.From);
    }

    [Fact]
    public async Task Vision_readings_do_not_show_up_in_the_habitat_view_nor_raise_environment_alerts()
    {
        await Sync(Crew(), [.. Series("iop", 15m, 28m), R("co2", 2m, 0)]);

        var env = await Mc(Permissions.VitalsView).GetFromJsonAsync<EnvironmentSummaryDto>($"/api/mc/v1/crew/{_astronautId}/environment", Json);
        Assert.Equal(["co2"], env!.Latest.Select(r => r.IndicatorId));
        Assert.Equal(1, env.History.Total);

        var alerts = await Mc(Permissions.AlertsView).GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts?state=all", Json);
        Assert.Empty(alerts!.Items);                                     // a critical IOP is shown, but is not an environment alert
    }

    [Fact]
    public async Task A_critical_iop_reaches_the_unified_risk_status_under_gravity_fields()
    {
        await Sync(Crew(), R("iop", 27m, 0));

        var risk = await Mc(Permissions.VitalsView).GetFromJsonAsync<RiskStatus>($"/api/mc/v1/crew/{_astronautId}/risk", Json);

        var gravity = risk!.Hazards.Single(h => h.Hazard == Hazard.GravityFields);
        Assert.Equal(HealthStatus.Critical, gravity.Status);
        Assert.Contains("Presión intraocular", gravity.Explanation);
        Assert.Equal(HealthStatus.Critical, risk.Status);
    }

    [Fact]
    public async Task Retrying_a_sync_does_not_duplicate_readings_and_ids_of_other_astronauts_are_rejected()
    {
        var reading = R("iop", 16m, 1);
        await Sync(Crew(), reading);
        var retry = await Sync(Crew(), reading);
        Assert.Contains(reading.Id, retry.Accepted);
        Assert.Equal(1, (await Vision()).Iop.Count);

        var taken = await Sync(Crew(_otherAstronautId), reading);
        Assert.Contains(taken.Rejected, r => r.Id == reading.Id);
        Assert.Equal(0, (await Vision(id: _otherAstronautId)).Iop.Count);
    }

    [Fact]
    public async Task Out_of_band_values_are_stored_as_watch_instead_of_failing_the_sync()
    {
        var sync = await Sync(Crew(), R("iop", 6m, 0), R("co2", -1m, 0));

        Assert.Equal(2, sync.Accepted.Count);
        Assert.Equal(HealthStatus.Watch, (await Vision()).Iop.Status);
    }

    [Fact]
    public async Task Readings_of_one_astronaut_do_not_show_for_another()
    {
        await Sync(Crew(_otherAstronautId), R("iop", 20m, 0));

        Assert.Equal(0, (await Vision()).Iop.Count);
        Assert.Equal(1, (await Vision(id: _otherAstronautId)).Iop.Count);
    }

    [Fact]
    public async Task From_after_to_is_a_400_unknown_astronaut_a_404_and_permissions_are_enforced()
    {
        var from = Uri.EscapeDataString(Today.ToString("O"));
        var to = Uri.EscapeDataString(Today.AddDays(-1).ToString("O"));
        Assert.Equal(HttpStatusCode.BadRequest, (await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{_astronautId}/vision?from={from}&to={to}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/vision")).StatusCode);

        var path = $"/api/mc/v1/crew/{_astronautId}/vision";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.AlertsView).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_server_and_the_shared_analysis_agree()
    {
        await Sync(Crew(), [.. Series("iop", 15m, 17m, 16m, 18m, 16.5m), .. Series("co2", 2m, 3.5m, 2.5m, 3m, 4m)]);

        var server = await Vision();

        // What the desktop computes offline from the same readings.
        var offline = VisionAnalysis.Analyze(
            server.Iop.Readings, [], Series("co2", 2m, 3.5m, 2.5m, 3m, 4m).Select(r => new VisionPoint(r.RecordedAt, r.Value)));

        Assert.Equal(offline.Iop.Direction, server.Iop.Direction);
        Assert.Equal(offline.Iop.SlopePerDay, server.Iop.SlopePerDay);
        Assert.Equal(offline.IopVsCo2.R, server.IopVsCo2.R);
        Assert.Equal(offline.IopVsCo2.Explanation, server.IopVsCo2.Explanation);
    }
}
