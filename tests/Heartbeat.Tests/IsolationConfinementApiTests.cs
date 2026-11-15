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

/// <summary>HB-BE-06 (#111): isolation and confinement indices in GET /mission and in RiskStatus.</summary>
public class IsolationConfinementApiTests : IDisposable
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

    private static readonly EnvironmentProfile Iss = new("station", 0m, 408m, 4, 388m);
    private static readonly EnvironmentProfile Mars = new("spacecraft", 0m, 225_000_000m, 4, 100m);

    private readonly Factory _factory = new();
    private readonly Guid _missionId = Guid.NewGuid();
    private readonly Guid _astronautId = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public IsolationConfinementApiTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Missions.Add(new Mission
        {
            Id = _missionId,
            Name = "Transit",
            Environment = "spacecraft",
            EnvironmentType = "spacecraft",
            GravityG = Mars.GravityG,
            DistanceKm = Mars.DistanceKm,
            CrewSize = Mars.CrewSize,
            HabitableVolumeM3 = Mars.HabitableVolumeM3,
            StartsAt = DateTimeOffset.UtcNow.AddDays(-30),
        });
        db.Astronauts.Add(new Astronaut
        {
            Id = _astronautId, UserName = "ana", DisplayName = "Ana", MissionId = _missionId, UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private HttpClient Client(string audience, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(_astronautId, "Someone", audience, permissions: permissions));
        return client;
    }

    private HttpClient Crew() => Client(AuthConstants.CrewAudience);

    private HttpClient Mc() => Client(AuthConstants.McAudience, Permissions.CrewView);

    private static async Task<MissionProfileDto> Read(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<MissionProfileDto>(Json))!;
    }

    // ----- Shared: the indices and the traffic light -----

    [Fact]
    public void A_station_in_low_earth_orbit_has_low_indices()
    {
        var indices = IsolationConfinement.Evaluate(Iss);

        Assert.Equal(0.15m, indices.Isolation.Value);
        Assert.Equal(0.0258m, indices.Confinement.Value);
        Assert.Equal(HealthStatus.Normal, indices.Isolation.Status);
        Assert.Equal(HealthStatus.Normal, indices.Confinement.Status);
        Assert.Equal(HealthStatus.Normal, indices.Status);
        Assert.True(indices.Simulated);
    }

    [Fact]
    public void A_transit_to_mars_raises_isolation_to_watch_from_distance_and_delay()
    {
        var indices = IsolationConfinement.Evaluate(Mars);

        Assert.InRange(indices.Isolation.Value, 0.47m, 0.48m);
        Assert.Equal(HealthStatus.Watch, indices.Isolation.Status);
        Assert.Equal(0.1m, indices.Confinement.Value);
        Assert.Equal(HealthStatus.Normal, indices.Confinement.Status);
        Assert.Equal(HealthStatus.Watch, indices.Status);
    }

    [Fact]
    public void Time_inputs_move_the_indices_and_are_echoed()
    {
        var indices = IsolationConfinement.Evaluate(Mars, hoursWithoutContact: 24m, continuousStayHours: 24m);

        Assert.InRange(indices.Isolation.Value, 0.67m, 0.68m);
        Assert.Equal(HealthStatus.Critical, indices.Isolation.Status);
        Assert.Equal(0.6m, indices.Confinement.Value);
        Assert.Equal(HealthStatus.Watch, indices.Confinement.Status);
        Assert.Equal(HealthStatus.Critical, indices.Status);
        Assert.Equal((24m, 24m), (indices.HoursWithoutContact, indices.ContinuousStayHours));
    }

    [Fact]
    public void A_tiny_habitat_after_a_long_stay_is_critical_confinement()
    {
        var indices = IsolationConfinement.Evaluate(new EnvironmentProfile("pod", 0m, 400m, 4, 4m), continuousStayHours: 24m);

        Assert.Equal(1m, indices.Confinement.Value);
        Assert.Equal(HealthStatus.Critical, indices.Confinement.Status);
        Assert.Equal(HealthStatus.Critical, indices.Status);
    }

    [Fact]
    public void Each_index_carries_its_catalog_status_and_source()
    {
        var indices = IsolationConfinement.Evaluate(Mars);

        Assert.Equal("isolation", indices.Isolation.IndicatorId);
        Assert.Equal("confinement", indices.Confinement.IndicatorId);
        Assert.Equal(CatalogStatus.Heuristic, indices.Isolation.CatalogStatus);
        Assert.Equal(CatalogStatus.Heuristic, indices.Confinement.CatalogStatus);
        Assert.Contains("Communication Delay", indices.Isolation.Source.Title);
        Assert.Contains("NASA", indices.Confinement.Source.Title);
        Assert.False(string.IsNullOrWhiteSpace(indices.Isolation.Label));
    }

    [Fact]
    public void Evaluate_requires_a_crew()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IsolationConfinement.Evaluate(Mars with { CrewSize = 0 }));
    }

    [Fact]
    public void The_hazard_status_takes_the_worse_index_with_its_critical_limit_and_source()
    {
        var hazard = IsolationConfinement.ToHazardStatus(IsolationConfinement.Evaluate(Mars));

        Assert.Equal(Hazard.IsolationAndConfinement, hazard.Hazard);
        Assert.Equal(HealthStatus.Watch, hazard.Status);
        Assert.InRange(hazard.Value!.Value, 0.47m, 0.48m);
        Assert.Equal(0.66m, hazard.Limit);
        Assert.Equal("0-1", hazard.Unit);
        Assert.Contains("Communication Delay", hazard.Source!.Title);
        Assert.Contains("Simulated", hazard.Explanation);
    }

    private static RiskStatus Risk(HealthStatus radiation, HealthStatus isolation) => new(
        (HealthStatus)Math.Max((int)radiation, (int)isolation),
        "Engine explanation.",
        [
            new HazardStatus(Hazard.SpaceRadiation, radiation, "Radiation."),
            new HazardStatus(Hazard.IsolationAndConfinement, isolation, "Old conduct."),
        ]);

    [Fact]
    public void Risk_status_gets_the_indices_and_replaces_only_the_conduct_hazard()
    {
        var risk = Risk(HealthStatus.Normal, HealthStatus.Normal)
            .WithIsolationConfinement(IsolationConfinement.Evaluate(Mars));

        Assert.Equal(2, risk.Hazards.Count);
        Assert.Equal(HealthStatus.Normal, risk.Hazards.Single(h => h.Hazard == Hazard.SpaceRadiation).Status);
        var conduct = risk.Hazards.Single(h => h.Hazard == Hazard.IsolationAndConfinement);
        Assert.Equal(HealthStatus.Watch, conduct.Status);
        Assert.DoesNotContain("Old conduct", conduct.Explanation);
        Assert.NotNull(risk.Indices);
        Assert.Equal(HealthStatus.Watch, risk.Indices!.Status);
    }

    [Fact]
    public void Risk_status_global_follows_the_worst_hazard_and_explains_a_new_worst()
    {
        var raised = Risk(HealthStatus.Normal, HealthStatus.Normal)
            .WithIsolationConfinement(IsolationConfinement.Evaluate(Mars));
        Assert.Equal(HealthStatus.Watch, raised.Status);
        Assert.Contains("Isolation", raised.Explanation);

        var kept = Risk(HealthStatus.Critical, HealthStatus.Normal)
            .WithIsolationConfinement(IsolationConfinement.Evaluate(Mars));
        Assert.Equal(HealthStatus.Critical, kept.Status);
        Assert.Equal("Engine explanation.", kept.Explanation);

        var lowered = Risk(HealthStatus.Normal, HealthStatus.Critical)
            .WithIsolationConfinement(IsolationConfinement.Evaluate(Iss));
        Assert.Equal(HealthStatus.Normal, lowered.Status);
    }

    [Fact]
    public void Risk_status_adds_the_conduct_hazard_when_the_engine_did_not_report_it()
    {
        var risk = new RiskStatus(
            HealthStatus.Normal, "ok", [new HazardStatus(Hazard.SpaceRadiation, HealthStatus.Normal, "Radiation.")])
            .WithIsolationConfinement(IsolationConfinement.Evaluate(Iss));

        Assert.Contains(risk.Hazards, h => h.Hazard == Hazard.IsolationAndConfinement);
        Assert.Equal(2, risk.Hazards.Count);
    }

    // ----- API: GET /mission (Mission Control and crew) -----

    [Fact]
    public async Task Mc_mission_includes_both_indices()
    {
        var mission = await Read(await Mc().GetAsync("/api/mc/v1/mission"));

        Assert.NotNull(mission.Indices);
        Assert.InRange(mission.Indices!.Isolation.Value, 0.47m, 0.48m);
        Assert.Equal(HealthStatus.Watch, mission.Indices.Isolation.Status);
        Assert.Equal(0.1m, mission.Indices.Confinement.Value);
        Assert.Equal(HealthStatus.Watch, mission.Indices.Status);
        Assert.True(mission.Indices.Simulated);
        Assert.Equal(CatalogStatus.Heuristic, mission.Indices.Isolation.CatalogStatus);
        Assert.InRange(mission.CommunicationDelaySeconds, 750.0, 751.0);
    }

    [Fact]
    public async Task Crew_mission_includes_the_same_indices()
    {
        var mc = await Read(await Mc().GetAsync("/api/mc/v1/mission"));
        var crew = await Read(await Crew().GetAsync("/api/crew/v1/mission"));

        Assert.Equal(mc.Indices, crew.Indices);
    }

    [Fact]
    public async Task Time_inputs_in_the_query_reach_the_indices_in_both_apps()
    {
        const string query = "?hoursWithoutContact=24&continuousStayHours=24";

        var mc = await Read(await Mc().GetAsync($"/api/mc/v1/mission{query}"));
        var crew = await Read(await Crew().GetAsync($"/api/crew/v1/mission{query}"));

        Assert.Equal(HealthStatus.Critical, mc.Indices!.Isolation.Status);
        Assert.Equal(0.6m, mc.Indices.Confinement.Value);
        Assert.Equal((24m, 24m), (mc.Indices.HoursWithoutContact, mc.Indices.ContinuousStayHours));
        Assert.Equal(mc.Indices, crew.Indices);
    }

    [Fact]
    public async Task Indices_are_serialized_as_camel_case_with_enum_strings()
    {
        var body = await Mc().GetStringAsync("/api/mc/v1/mission");

        Assert.Contains("\"indices\":{\"isolation\":{\"indicatorId\":\"isolation\"", body);
        Assert.Contains("\"catalogStatus\":\"heuristic\"", body);
        Assert.Contains("\"status\":\"watch\"", body);
        Assert.Contains("\"simulated\":true", body);
    }

    [Theory]
    [InlineData("?hoursWithoutContact=-1")]
    [InlineData("?continuousStayHours=-0.5")]
    public async Task Negative_time_inputs_are_400_in_both_apps(string query)
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await Mc().GetAsync($"/api/mc/v1/mission{query}")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Crew().GetAsync($"/api/crew/v1/mission{query}")).StatusCode);
    }

    [Fact]
    public async Task A_mission_without_crew_has_no_indices()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Missions.Where(m => m.Id == _missionId).ExecuteUpdateAsync(s => s.SetProperty(m => m.CrewSize, 0));
        }

        var mission = await Read(await Mc().GetAsync("/api/mc/v1/mission"));

        Assert.Null(mission.Indices);
    }

    [Fact]
    public async Task Mission_requires_the_crew_view_permission()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/mc/v1/mission")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Client(AuthConstants.McAudience, Permissions.AlertsView).GetAsync("/api/mc/v1/mission")).StatusCode);
    }
}
