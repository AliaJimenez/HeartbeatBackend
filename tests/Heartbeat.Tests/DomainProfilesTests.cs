using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

/// <summary>HB-BE-11 (#120): commercial domain profiles reuse the five hazards with other thresholds.</summary>
public class DomainProfilesTests : IDisposable
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

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static DomainProfile Profile(string id) => DomainProfiles.Find(id) ?? throw new InvalidOperationException(id);

    private static HealthStatus StatusOf(string profile, string indicator, decimal value)
        => DomainProfiles.Evaluate(Profile(profile), indicator, value).Status;

    private HttpClient Client(string audience, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(Guid.NewGuid(), "Someone", audience, permissions: permissions));
        return client;
    }

    // ----- Shared: the configuration -----

    [Fact]
    public void The_catalog_defines_the_reference_profile_and_the_four_commercial_ones()
    {
        Assert.Equal(["space", "mining", "antarctica", "submarine", "offshore"], DomainProfiles.All.Select(p => p.Id));
        Assert.Equal(DomainProfiles.All.Count, DomainProfiles.All.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void The_space_profile_is_the_catalog_itself()
    {
        var space = Profile(DomainProfiles.SpaceId);

        Assert.Empty(space.Overrides);
        Assert.Equal(IndicatorsCatalog.Cached.Select(e => (e.Id, e.Bands)), DomainProfiles.Effective(space).Select(e => (e.Id, e.Bands)));
    }

    [Fact]
    public void Commercial_profiles_override_thresholds_with_their_own_heuristic_status_and_source()
    {
        foreach (var profile in DomainProfiles.All.Where(p => p.Id != DomainProfiles.SpaceId))
        {
            Assert.NotEmpty(profile.Overrides);
            Assert.Equal(CatalogStatus.Heuristic, profile.Status);
            Assert.True(profile.Environment.CrewSize > 0);
            Assert.True(profile.Environment.GravityG > 0m);

            var effective = DomainProfiles.Effective(profile).ToDictionary(e => e.Id);
            foreach (var overridden in profile.Overrides.Keys)
            {
                Assert.Equal(profile.SourceId, effective[overridden].Source);
                Assert.Equal(CatalogStatus.Heuristic, effective[overridden].Status);
            }
        }
    }

    [Fact]
    public void The_same_value_gets_another_status_under_another_profile()
    {
        // noise (dB): space watch from 55 and critical from 70, mining normal below 85, offshore watch 80 to 90
        Assert.Equal(HealthStatus.Critical, StatusOf("space", "noise", 80m));
        Assert.Equal(HealthStatus.Normal, StatusOf("mining", "noise", 80m));
        Assert.Equal(HealthStatus.Watch, StatusOf("offshore", "noise", 80m));

        // co2 (mmHg): space watch from 3, submarine normal below 3.8
        Assert.Equal(HealthStatus.Watch, StatusOf("space", "co2", 3.5m));
        Assert.Equal(HealthStatus.Normal, StatusOf("submarine", "co2", 3.5m));

        // earthContact (min/day): space critical below 5, Antarctica watch from 2, submarine normal from 2
        Assert.Equal(HealthStatus.Critical, StatusOf("space", "earthContact", 3m));
        Assert.Equal(HealthStatus.Watch, StatusOf("antarctica", "earthContact", 3m));
        Assert.Equal(HealthStatus.Normal, StatusOf("submarine", "earthContact", 3m));

        // pressure (kPa): normal at a polar plateau station, critical at sea-level thresholds
        Assert.Equal(HealthStatus.Critical, StatusOf("space", "pressure", 68m));
        Assert.Equal(HealthStatus.Normal, StatusOf("antarctica", "pressure", 68m));
    }

    [Fact]
    public void Band_edges_follow_the_interval_rule_min_included_max_excluded()
    {
        Assert.Equal(HealthStatus.Normal, StatusOf("mining", "noise", 84.99m));
        Assert.Equal(HealthStatus.Watch, StatusOf("mining", "noise", 85m));
        Assert.Equal(HealthStatus.Critical, StatusOf("mining", "noise", 100m));
    }

    [Fact]
    public void An_indicator_a_profile_does_not_override_keeps_the_catalog_bands()
    {
        var mining = Profile("mining");

        Assert.DoesNotContain("iop", mining.Overrides.Keys);
        Assert.Equal(
            IndicatorsCatalog.Find("iop")!.Bands,
            DomainProfiles.Effective(mining).Single(e => e.Id == "iop").Bands);
    }

    [Fact]
    public void Evaluating_an_unknown_indicator_throws()
    {
        Assert.Throws<ArgumentException>(() => DomainProfiles.Evaluate(Profile("mining"), "cholesterol", 1m));
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_safe()
    {
        Assert.Equal("mining", DomainProfiles.Find("MINING")!.Id);
        Assert.Null(DomainProfiles.Find("nope"));
        Assert.Null(DomainProfiles.Find(null));
    }

    [Fact]
    public void The_dto_has_the_five_hazards_with_their_indicators_and_the_overridden_flags()
    {
        var dto = DomainProfiles.ToDto(Profile("submarine"));

        Assert.Equal(Enum.GetValues<Hazard>(), dto.Hazards.Select(h => h.Hazard));
        Assert.True(dto.Simulated);
        Assert.Equal(CatalogStatus.Heuristic, dto.Status);
        Assert.Contains("NEEMO", dto.Source.Title);

        var closed = dto.Hazards.Single(h => h.Hazard == Hazard.HostileClosedEnvironment).Indicators;
        var co2 = closed.Single(i => i.IndicatorId == "co2");
        Assert.True(co2.Overridden);
        Assert.Equal(new ProfileBandDto(HealthStatus.Watch, 3.8m, 6m), co2.Bands[1]);
        Assert.Equal("mmHg", co2.Unit);
        Assert.Contains("NEEMO", co2.Source.Title);
        Assert.False(closed.Single(i => i.IndicatorId == "noise").Overridden);

        Assert.Empty(dto.Hazards.Single(h => h.Hazard == Hazard.DistanceFromEarth).Indicators);
        Assert.Contains(dto.Hazards.Single(h => h.Hazard == Hazard.IsolationAndConfinement).Indicators, i => i.IndicatorId == "isolation");
    }

    [Fact]
    public void Bands_are_listed_in_ascending_order()
    {
        foreach (var profile in DomainProfiles.All.Select(DomainProfiles.ToDto))
        {
            foreach (var indicator in profile.Hazards.SelectMany(h => h.Indicators))
            {
                var mins = indicator.Bands.Select(b => b.Min ?? decimal.MinValue).ToList();
                Assert.Equal(mins.OrderBy(m => m), mins);
            }
        }
    }

    // ----- API: GET /api/mc/v1/profiles -----

    [Fact]
    public async Task Profiles_lists_the_five_profiles_to_any_signed_in_mission_control_user()
    {
        var res = await Client(AuthConstants.McAudience).GetAsync("/api/mc/v1/profiles");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var profiles = (await res.Content.ReadFromJsonAsync<List<DomainProfileDto>>(Json))!;
        Assert.Equal(["space", "mining", "antarctica", "submarine", "offshore"], profiles.Select(p => p.Id));
        Assert.All(profiles, p =>
        {
            Assert.True(p.Simulated);
            Assert.Equal(5, p.Hazards.Count);
            Assert.NotEmpty(p.Description);
        });
        var mining = profiles.Single(p => p.Id == "mining");
        Assert.Equal("mine", mining.Environment.Type);
        Assert.Equal(new ProfileBandDto(HealthStatus.Watch, 85m, 100m),
            mining.Hazards.Single(h => h.Hazard == Hazard.HostileClosedEnvironment).Indicators.Single(i => i.IndicatorId == "noise").Bands[1]);
    }

    [Fact]
    public async Task Profiles_are_serialized_as_camel_case_with_enum_strings()
    {
        var body = await Client(AuthConstants.McAudience).GetStringAsync("/api/mc/v1/profiles");

        Assert.Contains("\"hazard\":\"hostileClosedEnvironment\"", body);
        Assert.Contains("\"catalogStatus\":\"heuristic\"", body);
        Assert.Contains("\"overridden\":true", body);
        Assert.Contains("\"simulated\":true", body);
    }

    [Fact]
    public async Task Profiles_require_a_mission_control_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/mc/v1/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(AuthConstants.CrewAudience).GetAsync("/api/mc/v1/profiles")).StatusCode);
    }
}
