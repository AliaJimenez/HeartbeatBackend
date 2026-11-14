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

public class McAlertsEndpointsTests : IDisposable
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

    // One factory (and database) per test: alerts counts and acknowledgements must not leak between tests.
    private readonly Factory _factory = new();
    private readonly Guid _astronautId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();
    private readonly Guid _openOld = Guid.NewGuid();
    private readonly Guid _openNew = Guid.NewGuid();
    private readonly Guid _acked = Guid.NewGuid();

    public void Dispose() => _factory.Dispose();

    public McAlertsEndpointsTests()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var role = new Role { Id = Guid.NewGuid(), Name = "R" + Guid.NewGuid().ToString("N")[..6] };
        var mission = new Mission { Id = Guid.NewGuid(), Name = "Demo", StartsAt = T0 };
        db.Roles.Add(role);
        db.Missions.Add(mission);
        db.PortalUsers.Add(new PortalUser { Id = _surgeonId, UserName = "dr" + _surgeonId.ToString("N")[..6], Email = "dr@x.io", DisplayName = "Dr. Okafor", RoleId = role.Id });
        db.Astronauts.Add(new Astronaut { Id = _astronautId, UserName = "ana", DisplayName = "Ana Rivera", MissionId = mission.Id, UpdatedAt = T0 });

        Alert A(Guid id, int day, string indicators, bool ack)
        {
            var checkUp = new CheckUp { Id = Guid.NewGuid(), AstronautId = _astronautId, RecordedAt = T0.AddDays(day), OverallStatus = HealthStatus.Critical, RangesVersion = "v1" };
            db.CheckUps.Add(checkUp);
            return new Alert
            {
                Id = id, AstronautId = _astronautId, CheckUpId = checkUp.Id, Indicators = indicators, CreatedAt = T0.AddDays(day),
                AcknowledgedAt = ack ? T0.AddDays(day).AddHours(1) : null,
                AcknowledgedByUserId = ack ? _surgeonId : null,
                AcknowledgeNote = ack ? "Rest 24h" : null,
            };
        }

        db.SaveChanges();
        db.Alerts.AddRange(A(_openOld, 1, "BloodPressure", false), A(_openNew, 2, "BloodPressure,HeartRate", false), A(_acked, 3, "BodyTemperature", true));
        db.SaveChanges();
    }

    private HttpClient Client(Guid? userId = null, params string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId ?? Guid.NewGuid(), "Dr", AuthConstants.McAudience, permissions: permissions));
        return client;
    }

    private Task<Paged<AlertDto>> List(string query = "")
        => Client(null, Permissions.AlertsView).GetFromJsonAsync<Paged<AlertDto>>($"/api/mc/v1/alerts{query}", Json)!;

    [Fact]
    public async Task Endpoints_require_their_permissions()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/mc/v1/alerts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client(null, Permissions.CrewView).GetAsync("/api/mc/v1/alerts")).StatusCode);

        var body = new AcknowledgeAlertRequest("ok");
        var res = await Client(_surgeonId, Permissions.AlertsView).PostAsJsonAsync($"/api/mc/v1/alerts/{_openOld}/acknowledge", body);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task List_defaults_to_open_newest_first_with_astronaut_and_indicators()
    {
        var page = await List();
        Assert.Equal(2, page.Total);
        Assert.Equal([_openNew, _openOld], page.Items.Select(a => a.Id));
        var first = page.Items[0];
        Assert.Equal("Ana Rivera", first.AstronautName);
        Assert.Equal([IndicatorType.BloodPressure, IndicatorType.HeartRate], first.Indicators);
        Assert.Null(first.AcknowledgedAt);
    }

    [Fact]
    public async Task List_filters_by_state()
    {
        var acked = await List("?state=acknowledged");
        var alert = Assert.Single(acked.Items);
        Assert.Equal(_acked, alert.Id);
        Assert.Equal("Dr. Okafor", alert.AcknowledgedBy);
        Assert.Equal("Rest 24h", alert.AcknowledgeNote);
        Assert.NotNull(alert.AcknowledgedAt);

        Assert.Equal(3, (await List("?state=all")).Total);
        Assert.Equal(2, (await List("?state=OPEN")).Total);
    }

    [Fact]
    public async Task List_pages_and_rejects_bad_parameters()
    {
        var page = await List("?state=all&page=2&pageSize=2");
        Assert.Equal((2, 2, 3), (page.Page, page.PageSize, page.Total));
        Assert.Single(page.Items);

        var http = Client(null, Permissions.AlertsView);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/mc/v1/alerts?state=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/mc/v1/alerts?pageSize=0")).StatusCode);
    }

    [Fact]
    public async Task Acknowledge_stores_who_when_and_note_and_serializes_camel_case()
    {
        var http = Client(_surgeonId, Permissions.AlertsAcknowledge, Permissions.AlertsView);
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        var res = await http.PostAsJsonAsync($"/api/mc/v1/alerts/{_openOld}/acknowledge", new AcknowledgeAlertRequest("  Recheck in 1h  "));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("\"indicators\":[\"bloodPressure\"]", body);

        var dto = JsonSerializer.Deserialize<AlertDto>(body, Json)!;
        Assert.Equal("Dr. Okafor", dto.AcknowledgedBy);
        Assert.Equal("Recheck in 1h", dto.AcknowledgeNote);
        Assert.True(dto.AcknowledgedAt >= before);

        using var scope = _factory.Services.CreateScope();
        var stored = scope.ServiceProvider.GetRequiredService<AppDbContext>().Alerts.Single(a => a.Id == _openOld);
        Assert.Equal(_surgeonId, stored.AcknowledgedByUserId);

        // It left the open list.
        Assert.DoesNotContain((await List()).Items, a => a.Id == _openOld);
    }

    [Fact]
    public async Task Acknowledge_twice_is_409_unknown_is_404_and_note_is_required()
    {
        var http = Client(_surgeonId, Permissions.AlertsAcknowledge);

        Assert.Equal(HttpStatusCode.Conflict,
            (await http.PostAsJsonAsync($"/api/mc/v1/alerts/{_acked}/acknowledge", new AcknowledgeAlertRequest("again"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync($"/api/mc/v1/alerts/{Guid.NewGuid()}/acknowledge", new AcknowledgeAlertRequest("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync($"/api/mc/v1/alerts/{_openNew}/acknowledge", new AcknowledgeAlertRequest("   "))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await http.PostAsJsonAsync($"/api/mc/v1/alerts/{_openNew}/acknowledge", new AcknowledgeAlertRequest(new string('x', 501)))).StatusCode);
    }

    [Fact]
    public async Task Acknowledge_by_unknown_user_is_401()
    {
        var res = await Client(Guid.NewGuid(), Permissions.AlertsAcknowledge)
            .PostAsJsonAsync($"/api/mc/v1/alerts/{_openNew}/acknowledge", new AcknowledgeAlertRequest("x"));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}
