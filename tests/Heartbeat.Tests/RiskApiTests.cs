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

/// <summary>HB-BE-07 (#103): GET /api/mc/v1/crew/{id}/risk, fed by vitals, radiation and habitat readings through sync.</summary>
public class RiskApiTests : IDisposable
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
    private readonly Guid _surgeonId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public RiskApiTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission
        {
            Id = Guid.NewGuid(), Name = "Mars", Environment = "marsBase", EnvironmentType = "marsBase",
            GravityG = 0.38m, DistanceKm = 225_000_000m, CrewSize = 4, HabitableVolumeM3 = 120m, StartsAt = Now.AddDays(-30),
        };
        var role = new Role { Id = Guid.NewGuid(), Name = "Surgeon" };
        db.Missions.Add(mission);
        db.Roles.Add(role);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "j.okafor", Email = "j@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id, IsActive = true });
        db.Astronauts.Add(new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana Rivera", MissionId = mission.Id, UpdatedAt = Now });
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

    private HttpClient Crew() => Authorized(AuthConstants.CrewAudience, _astronautId);

    private HttpClient Mc(params string[] permissions) => Authorized(AuthConstants.McAudience, _surgeonId, permissions);

    private Task<RiskStatus> Risk(string query = "")
        => Mc(Permissions.VitalsView).GetFromJsonAsync<RiskStatus>($"/api/mc/v1/crew/{_astronautId}/risk{query}", Json)!;

    private static CheckUpDto CheckUp(DateTimeOffset at, params (IndicatorType Type, decimal Value, decimal? Second)[] readings)
    {
        var evaluation = new HealthEvaluator().Evaluate(readings.Select(r => new ReadingInput(r.Type, r.Value, r.Second)));
        return new CheckUpDto(
            Guid.NewGuid(), at, null, evaluation.OverallStatus, evaluation.RangesVersion, null, null,
            evaluation.Readings.Select((e, i) => new ReadingDto(Guid.NewGuid(), e.Indicator, readings[i].Value, readings[i].Second, e.Status, e.BandIds)).ToList());
    }

    private async Task Sync(
        IReadOnlyList<CheckUpDto>? checkUps = null,
        IReadOnlyList<RadiationDoseDto>? doses = null,
        IReadOnlyList<EnvironmentReading>? env = null)
    {
        var request = new SyncRequest("dev", null, "2026.11.14-1", null, checkUps ?? [], [], doses, env);
        var res = await Crew().PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private static HazardStatus Of(RiskStatus risk, Hazard hazard) => risk.Hazards.Single(h => h.Hazard == hazard);

    [Fact]
    public async Task A_new_astronaut_only_carries_the_planned_conditions_of_the_mission()
    {
        var risk = await Risk();

        Assert.Equal(5, risk.Hazards.Count);
        Assert.Equal(HealthStatus.Normal, Of(risk, Hazard.SpaceRadiation).Status);
        Assert.Equal(HealthStatus.Normal, Of(risk, Hazard.HostileClosedEnvironment).Status);
        Assert.Equal(HealthStatus.Watch, Of(risk, Hazard.DistanceFromEarth).Status);   // Mars: 12.5 min delay, capped to watch
        Assert.Equal(HealthStatus.Watch, Of(risk, Hazard.GravityFields).Status);       // 0.38 g
        Assert.Equal(HealthStatus.Watch, risk.Status);
        Assert.NotNull(risk.Indices);
    }

    [Fact]
    public async Task Vitals_radiation_and_habitat_data_land_under_their_hazards_and_the_global_is_the_worst()
    {
        await Sync(
            [CheckUp(Now.AddMinutes(-10), (IndicatorType.BloodPressure, 190, 125), (IndicatorType.Sleep, 7.5m, null), (IndicatorType.BloodSugar, 95, null))],
            [new RadiationDoseDto(Guid.NewGuid(), Now.AddMinutes(-9), null, 350m, null, RadiationType.Gcr)],
            [new EnvironmentReading(Guid.NewGuid(), Now.AddMinutes(-8), null, "co2", 4m)]);

        var risk = await Risk();

        Assert.Equal(HealthStatus.Critical, Of(risk, Hazard.GravityFields).Status);              // blood pressure
        Assert.Equal(HealthStatus.Watch, Of(risk, Hazard.SpaceRadiation).Status);                // 58% of 600 mSv
        Assert.Equal(HealthStatus.Watch, Of(risk, Hazard.HostileClosedEnvironment).Status);      // CO2 4 mmHg
        Assert.Equal(risk.Indices!.Status, Of(risk, Hazard.IsolationAndConfinement).Status);     // sleep is fine; the Mars indices decide
        Assert.DoesNotContain("Sleep", Of(risk, Hazard.IsolationAndConfinement).Explanation);
        Assert.Equal(HealthStatus.Critical, risk.Status);
        Assert.StartsWith("Gravity fields is critical:", risk.Explanation);
        Assert.Contains("Blood pressure", Of(risk, Hazard.GravityFields).Explanation);
        Assert.NotNull(Of(risk, Hazard.GravityFields).Source);
    }

    [Fact]
    public async Task The_latest_value_of_each_vital_wins_over_older_check_ups()
    {
        await Sync([
            CheckUp(Now.AddHours(-3), (IndicatorType.HeartRate, 140, null)),
            CheckUp(Now.AddMinutes(-5), (IndicatorType.HeartRate, 62, null))]);

        var risk = await Risk();

        Assert.Equal(HealthStatus.Watch, Of(risk, Hazard.GravityFields).Status);   // only the planned 0.38 g warning remains
        Assert.DoesNotContain("Heart rate", Of(risk, Hazard.GravityFields).Explanation);
    }

    [Fact]
    public async Task A_solar_particle_event_turns_radiation_red_on_the_endpoint_too()
    {
        await Sync(doses: [new RadiationDoseDto(Guid.NewGuid(), Now.AddMinutes(-5), null, 260m, null, RadiationType.Spe)]);

        var hazard = Of(await Risk(), Hazard.SpaceRadiation);

        Assert.Equal(HealthStatus.Critical, hazard.Status);
        Assert.Contains("solar particle event", hazard.Explanation);
        Assert.Equal("mSv", hazard.Unit);
    }

    [Fact]
    public async Task Time_inputs_reach_the_isolation_indices()
    {
        var plain = await Risk();
        var long_ = await Risk("?hoursWithoutContact=24&continuousStayHours=24");

        Assert.True(long_.Indices!.Isolation.Value > plain.Indices!.Isolation.Value);
        Assert.True(long_.Indices.Confinement.Value > plain.Indices.Confinement.Value);
    }

    [Theory]
    [InlineData("?hoursWithoutContact=-1")]
    [InlineData("?continuousStayHours=-5")]
    public async Task Negative_time_inputs_are_a_400(string query)
    {
        var res = await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{_astronautId}/risk{query}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_domain_profile_changes_the_light_with_the_same_data()
    {
        await Sync(env: [new EnvironmentReading(Guid.NewGuid(), Now.AddMinutes(-8), null, "noise", 90m)]);   // 90 dB

        var standard = await Risk();
        var mining = await Risk("?profile=mining");

        Assert.Null(standard.ProfileId);
        Assert.Equal("mining", mining.ProfileId);
        Assert.Equal(HealthStatus.Critical, Of(standard, Hazard.HostileClosedEnvironment).Status);   // catalog: 70 dB and up
        Assert.Equal(HealthStatus.Watch, Of(mining, Hazard.HostileClosedEnvironment).Status);        // mining: 85 to 100 dB
    }

    [Fact]
    public async Task The_space_profile_and_no_profile_give_the_same_light()
    {
        await Sync(env: [new EnvironmentReading(Guid.NewGuid(), Now.AddMinutes(-8), null, "humidity", 75m)]);

        var standard = await Risk();
        var space = await Risk("?profile=SPACE");

        Assert.Equal(standard.Status, space.Status);
        Assert.Equal(standard.Hazards.Select(h => h.Status), space.Hazards.Select(h => h.Status));
    }

    [Fact]
    public async Task A_profile_also_moves_the_isolation_thresholds()
    {
        var standard = await Risk("?hoursWithoutContact=24");
        var antarctica = await Risk("?hoursWithoutContact=24&profile=antarctica");

        Assert.Equal(standard.Indices!.Isolation.Value, antarctica.Indices!.Isolation.Value);   // same index, other thresholds
        Assert.Equal("antarctica", antarctica.ProfileId);
        Assert.NotEqual(standard.Indices.Isolation.Source, antarctica.Indices.Isolation.Source);
    }

    [Fact]
    public async Task An_unknown_profile_is_a_400()
    {
        var res = await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{_astronautId}/risk?profile=nope");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_astronaut_is_a_404_and_the_endpoint_requires_vitals_view()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Mc(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/risk")).StatusCode);

        var path = $"/api/mc/v1/crew/{_astronautId}/risk";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(Permissions.AlertsView).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew().GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task The_server_and_the_shared_engine_agree()
    {
        await Sync(
            [CheckUp(Now.AddMinutes(-10), (IndicatorType.HeartRate, 130, null))],
            [new RadiationDoseDto(Guid.NewGuid(), Now.AddMinutes(-9), null, 100m, null, RadiationType.Gcr)]);

        var fromServer = await Risk();

        // What the desktop would compute offline from the same data.
        var offline = RiskEngine.Evaluate(new RiskInputs(
            new HealthEvaluator().Evaluate([new ReadingInput(IndicatorType.HeartRate, 130)]),
            fromServer.Hazards.Count == 5 ? Radiation(100m) : null,
            [],
            fromServer.Indices is null ? null : new EnvironmentProfile("marsBase", 0.38m, 225_000_000m, 4, 120m),
            fromServer.Indices));

        Assert.Equal(offline.Status, fromServer.Status);
        Assert.Equal(offline.Hazards.Select(h => (h.Hazard, h.Status, h.Explanation)), fromServer.Hazards.Select(h => (h.Hazard, h.Status, h.Explanation)));
    }

    private static RadiationSummaryDto Radiation(decimal total) => new(
        total, 1, [], ReferenceRanges.GetSource(RadiationDoses.SourceId), RadiationType.Gcr, 0.7m,
        RadiationDoses.PercentOfCareerLimit(total), 0m, 0m, RadiationDoses.StatusForCareerPercent(RadiationDoses.PercentOfCareerLimit(total)));
}
