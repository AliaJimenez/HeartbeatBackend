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

public class McVitalsEndpointsTests : IClassFixture<McVitalsEndpointsTests.Factory>
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
    private readonly Guid _okCheckUp = Guid.NewGuid();
    private readonly Guid _badCheckUp = Guid.NewGuid();
    private readonly Guid _wrongCheckUp = Guid.NewGuid();

    public McVitalsEndpointsTests(Factory factory)
    {
        _factory = factory;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = T0 };
        db.Missions.Add(mission);
        db.Astronauts.Add(new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana", MissionId = mission.Id, UpdatedAt = T0 });

        CheckUp C(Guid id, int day, HealthStatus s, Guid? supersedes = null) => new()
        {
            Id = id, AstronautId = _astronautId, RecordedAt = T0.AddDays(day), ReceivedAt = T0.AddDays(day).AddMinutes(5),
            OverallStatus = s, RangesVersion = "2026.11.14-1", SupersedesId = supersedes,
        };
        Reading R(Guid c, IndicatorType i, decimal v, decimal? v2, HealthStatus s, string bands) =>
            new() { Id = Guid.NewGuid(), CheckUpId = c, Indicator = i, Value = v, SecondaryValue = v2, Status = s, BandIds = bands };

        db.CheckUps.AddRange(
            C(_okCheckUp, 0, HealthStatus.Normal),
            C(_wrongCheckUp, 1, HealthStatus.Normal),
            C(Guid.NewGuid(), 2, HealthStatus.Normal, supersedes: _wrongCheckUp),
            C(_badCheckUp, 3, HealthStatus.Critical));
        db.Readings.AddRange(
            R(_okCheckUp, IndicatorType.HeartRate, 64, null, HealthStatus.Normal, "hr-normal"),
            R(_okCheckUp, IndicatorType.Sleep, 7, null, HealthStatus.Normal, "sleep-normal"),
            R(_badCheckUp, IndicatorType.BloodPressure, 185, 95, HealthStatus.Critical, "bp-sys-critical-high,bp-dia-watch-high"));
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

    [Fact]
    public async Task Endpoints_require_their_permission()
    {
        var path = $"/api/mc/v1/crew/{_astronautId}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync($"{path}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Permissions.VitalsHistory).GetAsync($"{path}/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Permissions.VitalsView).GetAsync($"{path}/checkups")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(Permissions.VitalsHistory).GetAsync($"{path}/checkups/{_okCheckUp}")).StatusCode);
    }

    [Fact]
    public async Task Status_returns_latest_value_of_each_indicator()
    {
        var res = await Client(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{_astronautId}/status");
        res.EnsureSuccessStatusCode();
        var dto = (await res.Content.ReadFromJsonAsync<AstronautStatusDto>(Json))!;

        Assert.Equal(HealthStatus.Critical, dto.OverallStatus);
        Assert.Equal(3, dto.Indicators.Count);
        var bp = dto.Indicators.Single(i => i.Indicator == IndicatorType.BloodPressure);
        Assert.Equal((185m, 95m), (bp.Value, bp.SecondaryValue));
        Assert.Equal("mmHg", bp.Unit);
        Assert.Equal("Very high systolic", bp.Label);
        Assert.Equal(_badCheckUp, bp.CheckUpId);
        Assert.Equal(_okCheckUp, dto.Indicators.Single(i => i.Indicator == IndicatorType.HeartRate).CheckUpId);
    }

    [Fact]
    public async Task Status_serializes_enums_as_camel_case_strings()
    {
        var body = await Client(Permissions.VitalsView).GetStringAsync($"/api/mc/v1/crew/{_astronautId}/status");
        Assert.Contains("\"overallStatus\":\"critical\"", body);
        Assert.Contains("\"indicator\":\"bloodPressure\"", body);
    }

    [Fact]
    public async Task Unknown_astronaut_is_404()
    {
        var res = await Client(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/status");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Checkups_are_newest_first_and_hide_superseded()
    {
        var page = (await Client(Permissions.VitalsHistory)
            .GetFromJsonAsync<Paged<CheckUpDto>>($"/api/mc/v1/crew/{_astronautId}/checkups", Json))!;

        Assert.Equal(3, page.Total); // 4 stored, 1 superseded
        Assert.DoesNotContain(page.Items, c => c.Id == _wrongCheckUp);
        Assert.Equal(_badCheckUp, page.Items[0].Id);
        Assert.Equal(_okCheckUp, page.Items[^1].Id);
        Assert.Single(page.Items[0].Readings);
        Assert.Equal(2, page.Items[^1].Readings.Count);
    }

    [Fact]
    public async Task Checkups_filter_by_status_and_date_and_page()
    {
        var http = Client(Permissions.VitalsHistory);
        var path = $"/api/mc/v1/crew/{_astronautId}/checkups";

        var critical = (await http.GetFromJsonAsync<Paged<CheckUpDto>>($"{path}?status=critical", Json))!;
        Assert.Equal(_badCheckUp, Assert.Single(critical.Items).Id);

        var from = Uri.EscapeDataString(T0.AddDays(1).ToString("o"));
        var to = Uri.EscapeDataString(T0.AddDays(2).ToString("o"));
        var range = (await http.GetFromJsonAsync<Paged<CheckUpDto>>($"{path}?from={from}&to={to}", Json))!;
        Assert.Equal(1, range.Total); // day 1 is superseded, day 2 is its correction

        var paged = (await http.GetFromJsonAsync<Paged<CheckUpDto>>($"{path}?page=2&pageSize=2", Json))!;
        Assert.Equal((2, 2, 3), (paged.Page, paged.PageSize, paged.Total));
        Assert.Single(paged.Items);
    }

    [Fact]
    public async Task Checkups_reject_unknown_status()
    {
        var res = await Client(Permissions.VitalsHistory).GetAsync($"/api/mc/v1/crew/{_astronautId}/checkups?status=bogus");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Checkups_reject_bad_paging()
    {
        var res = await Client(Permissions.VitalsHistory).GetAsync($"/api/mc/v1/crew/{_astronautId}/checkups?pageSize=0");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Detail_includes_evaluation_with_guidance_escalation_and_sources()
    {
        var dto = (await Client(Permissions.VitalsView)
            .GetFromJsonAsync<CheckUpDetailDto>($"/api/mc/v1/crew/{_astronautId}/checkups/{_badCheckUp}", Json))!;

        var eval = Assert.Single(dto.Evaluations);
        Assert.Equal(HealthStatus.Critical, eval.Status);
        Assert.Equal("Very high systolic", eval.Label);
        Assert.NotEmpty(eval.Guidance);
        Assert.Equal(EscalationContact.CrewMedicalOfficer, eval.Escalation!.Contact);
        Assert.NotEmpty(eval.Sources);
        Assert.Equal(["bp-sys-critical-high", "bp-dia-watch-high"], eval.BandIds);
    }

    [Fact]
    public async Task Detail_of_a_checkup_under_another_astronaut_is_404()
    {
        var res = await Client(Permissions.VitalsView).GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/checkups/{_okCheckUp}");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }
}
