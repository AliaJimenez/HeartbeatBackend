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

/// <summary>HB-BE-08 (#117): phantom comparison, the shared engine and GET /api/mc/v1/phantoms/compare.</summary>
public class PhantomComparisonTests : IDisposable
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

    private HttpClient Mc()
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(Guid.NewGuid(), "Someone", AuthConstants.McAudience, permissions: []));
        return client;
    }

    private Task<PhantomComparisonDto> Get(string query = "")
        => Mc().GetFromJsonAsync<PhantomComparisonDto>($"/api/mc/v1/phantoms/compare{query}", Json)!;

    private static PhantomEnvironment Station => PhantomComparison.FindEnvironment("station")!;

    // ---- shared engine --------------------------------------------------------------------------------------------

    [Fact]
    public void Compare_gives_one_series_per_phantom_with_a_point_for_every_week_from_zero()
    {
        var result = PhantomComparison.Compare(Phantoms.Profiles, Station, 12, 7m);

        Assert.Equal(Phantoms.Profiles.Count, result.Series.Count);
        Assert.All(result.Series, s =>
        {
            Assert.Equal(13, s.Points.Count);
            Assert.Equal(Enumerable.Range(0, 13), s.Points.Select(p => p.Week));
            Assert.Equal(12, s.Final.Week);
            Assert.Equal(6, s.Regions.Count);
        });
        Assert.Equal(12, result.Weeks);
        Assert.True(result.Simulated);
        Assert.Equal(CatalogStatus.Heuristic, result.Status);
        Assert.Contains("not a diagnosis", result.Note);
    }

    [Fact]
    public void Week_zero_is_no_change_and_the_phantom_profile_starts_unchanged()
    {
        var series = PhantomComparison.Compare([Phantoms.Profiles[0]], Station, 8, 0m).Series.Single();

        Assert.Equal(new PhantomPoint(0, 0m, 0m, 0m, 0m), series.Points[0]);
        Assert.Equal(0m, series.Phantom.BoneChangePercent);
        Assert.Equal(Phantoms.Profiles[0].Name, series.Phantom.Name);
    }

    [Fact]
    public void Every_point_matches_the_single_phantom_simulation()
    {
        var phantom = Phantoms.Profiles[2];
        var series = PhantomComparison.Compare([phantom], PhantomComparison.FindEnvironment("lunarBase")!, 20, 3m).Series.Single();

        foreach (var point in series.Points)
        {
            var sim = Phantoms.Simulate(phantom, 0.17m, point.Week, 3m).Phantom;
            Assert.Equal(sim.BoneChangePercent, point.BoneChangePercent);
            Assert.Equal(sim.MuscleChangePercent, point.MuscleChangePercent);
            Assert.Equal(sim.FluidChangePercent, point.FluidChangePercent);
            Assert.Equal(sim.IopChangeMmHg, point.IopChangeMmHg);
        }
    }

    [Fact]
    public void Bone_and_muscle_only_get_worse_week_after_week_in_microgravity()
    {
        var points = PhantomComparison.Compare([Phantoms.Profiles[1]], Station, 26, 0m).Series.Single().Points;

        for (var i = 1; i < points.Count; i++)
        {
            Assert.True(points[i].BoneChangePercent < points[i - 1].BoneChangePercent);
            Assert.True(points[i].MuscleChangePercent < points[i - 1].MuscleChangePercent);
            Assert.True(points[i].FluidChangePercent >= points[i - 1].FluidChangePercent);
        }
    }

    [Fact]
    public void The_same_stay_hits_phantoms_differently_by_sex_and_age()
    {
        var byName = PhantomComparison.Compare(Phantoms.Profiles, Station, 26, 0m).Series.ToDictionary(s => s.Phantom.Name, s => s.Final);

        Assert.True(byName["Phantom A"].BoneChangePercent < byName["Phantom D"].BoneChangePercent);   // female 35 loses more than male 28
        Assert.True(byName["Phantom C"].BoneChangePercent < byName["Phantom A"].BoneChangePercent);   // female 55 loses more than female 35
        Assert.Equal(byName["Phantom A"].MuscleChangePercent, byName["Phantom D"].MuscleChangePercent);   // muscle does not depend on sex
    }

    [Fact]
    public void Gravity_and_exercise_reduce_the_change()
    {
        var phantom = Phantoms.Profiles[0];
        PhantomPoint Final(string env, decimal exercise) => PhantomComparison.Compare([phantom], PhantomComparison.FindEnvironment(env)!, 26, exercise).Series.Single().Final;

        Assert.True(Final("lunarBase", 0m).BoneChangePercent < Final("marsBase", 0m).BoneChangePercent);   // less gravity, more loss
        Assert.True(Final("station", 0m).BoneChangePercent < Final("lunarBase", 0m).BoneChangePercent);
        Assert.True(Final("station", 10m).BoneChangePercent > Final("station", 0m).BoneChangePercent);    // exercise helps
        Assert.Equal(new PhantomPoint(26, 0m, 0m, 0m, 0m), PhantomComparison.Compare([phantom], PhantomComparison.FindEnvironment("mining")!, 26, 0m).Series.Single().Final);   // 1 g
    }

    [Fact]
    public void Compare_is_deterministic_and_drops_duplicate_phantoms()
    {
        var a = PhantomComparison.Compare([Phantoms.Profiles[0], Phantoms.Profiles[0], Phantoms.Profiles[1]], Station, 10, 7m);
        var b = PhantomComparison.Compare([Phantoms.Profiles[0], Phantoms.Profiles[1]], Station, 10, 7m);

        Assert.Equal(2, a.Series.Count);
        Assert.Equal(a.Series.Select(s => s.Final), b.Series.Select(s => s.Final));
    }

    [Theory]
    [InlineData("station", 0)]
    [InlineData("spacecraft", 0)]
    [InlineData("LUNARBASE", 0.17)]
    [InlineData("marsBase", 0.38)]
    [InlineData("mining", 1)]
    [InlineData("space", 0)]
    public void Environments_come_from_the_catalog_or_a_domain_profile(string key, double gravity)
        => Assert.Equal((decimal)gravity, PhantomComparison.FindEnvironment(key)!.GravityG);

    [Theory]
    [InlineData("")]
    [InlineData("jupiter")]
    [InlineData(null)]
    public void Unknown_environments_are_null(string? key) => Assert.Null(PhantomComparison.FindEnvironment(key));

    [Fact]
    public void Phantoms_are_found_by_catalog_id_name_or_guid()
    {
        var a = Phantoms.Profiles[0];

        Assert.Equal(a, PhantomComparison.FindPhantom("phantom-f35"));
        Assert.Equal(a, PhantomComparison.FindPhantom("phantom a"));
        Assert.Equal(a, PhantomComparison.FindPhantom(a.Id.ToString()));
        Assert.Null(PhantomComparison.FindPhantom("phantom z"));
    }

    [Fact]
    public void The_defaults_and_environments_live_in_the_catalog()
    {
        using var doc = IndicatorsCatalog.Parse();
        var model = doc.RootElement.GetProperty("phantomModel");

        Assert.Equal("station", PhantomComparison.Defaults.Environment);
        Assert.Equal(26, PhantomComparison.Defaults.Weeks);
        Assert.Equal(7m, PhantomComparison.Defaults.ExerciseHoursPerWeek);
        Assert.Equal(4, model.GetProperty("environments").EnumerateObject().Count());
        Assert.Equal("heuristic", model.GetProperty("status").GetString());
    }

    // ---- endpoint -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Without_parameters_it_compares_every_phantom_in_the_default_stay()
    {
        var result = await Get();

        Assert.Equal(Phantoms.Profiles.Select(p => p.Name), result.Series.Select(s => s.Phantom.Name));
        Assert.Equal("station", result.Environment);
        Assert.Equal(26, result.Weeks);
        Assert.Equal(7m, result.ExerciseHoursPerWeek);
        Assert.All(result.Series, s => Assert.Equal(27, s.Points.Count));
        Assert.StartsWith("https://", result.Source.Url.ToString());
        Assert.True(result.Simulated);
    }

    [Fact]
    public async Task Profiles_environment_weeks_and_exercise_shape_the_series()
    {
        var result = await Get("?profiles=phantom-f35,Phantom C&environment=marsBase&weeks=10&exerciseHours=0");

        Assert.Equal(["Phantom A", "Phantom C"], result.Series.Select(s => s.Phantom.Name));
        Assert.Equal("marsBase", result.Environment);
        Assert.Equal(0.38m, result.GravityG);
        Assert.Equal(10, result.Weeks);
        Assert.Equal(0m, result.ExerciseHoursPerWeek);
        Assert.All(result.Series, s => Assert.Equal(11, s.Points.Count));

        var expected = PhantomComparison.Compare(
            [PhantomComparison.FindPhantom("Phantom A")!, PhantomComparison.FindPhantom("Phantom C")!],
            PhantomComparison.FindEnvironment("marsBase")!, 10, 0m);
        Assert.Equal(expected.Series.Select(s => s.Final), result.Series.Select(s => s.Final));
    }

    [Fact]
    public async Task The_endpoint_and_the_shared_engine_agree_for_the_same_input()
    {
        var server = await Get("?environment=lunarBase&weeks=14&exerciseHours=3.5");
        var offline = PhantomComparison.Compare(Phantoms.Profiles, PhantomComparison.FindEnvironment("lunarBase")!, 14, 3.5m);

        Assert.Equal(offline.Series.Select(s => s.Points), server.Series.Select(s => s.Points));
        Assert.Equal(offline.Series.Select(s => s.Regions), server.Series.Select(s => s.Regions));
    }

    [Theory]
    [InlineData("?profiles=nobody")]
    [InlineData("?profiles=phantom-f35,nobody")]
    [InlineData("?profiles=,")]
    [InlineData("?environment=jupiter")]
    [InlineData("?weeks=0")]
    [InlineData("?weeks=105")]
    [InlineData("?exerciseHours=-1")]
    [InlineData("?exerciseHours=41")]
    public async Task Invalid_parameters_are_a_400_with_the_field_named(string query)
    {
        var res = await Mc().GetAsync($"/api/mc/v1/phantoms/compare{query}");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("errors", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_limits_themselves_are_accepted()
    {
        Assert.Equal(HttpStatusCode.OK, (await Mc().GetAsync("/api/mc/v1/phantoms/compare?weeks=104&exerciseHours=40")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Mc().GetAsync("/api/mc/v1/phantoms/compare?weeks=1&exerciseHours=0")).StatusCode);
    }

    [Fact]
    public async Task It_needs_a_signed_in_mission_control_user_and_rejects_crew_tokens()
    {
        var path = "/api/mc/v1/phantoms/compare";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);

        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var crew = _factory.CreateClient();
        crew.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(Guid.NewGuid(), "Someone", AuthConstants.CrewAudience, permissions: []));
        Assert.Equal(HttpStatusCode.Forbidden, (await crew.GetAsync(path)).StatusCode);
    }
}
