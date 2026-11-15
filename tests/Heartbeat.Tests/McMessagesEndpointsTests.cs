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

/// <summary>MSG-BACKEND (#24): both directions of the conversation, end to end through POST /sync.</summary>
public class McMessagesEndpointsTests : IDisposable
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

    // One database per test: message counts and read state must not leak between tests.
    private readonly Factory _factory = new();
    private readonly Guid _astronautId = Guid.NewGuid();
    private readonly Guid _otherAstronautId = Guid.NewGuid();
    private readonly Guid _surgeonId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    public void Dispose() => _factory.Dispose();

    public McMessagesEndpointsTests()
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

    private HttpClient Mc(Guid? userId = null, params string[] permissions)
        => Authorized(AuthConstants.McAudience, userId ?? _surgeonId, permissions);

    private HttpClient Crew(Guid? astronautId = null)
        => Authorized(AuthConstants.CrewAudience, astronautId ?? _astronautId, []);

    private HttpClient Authorized(string audience, Guid userId, string[] permissions)
    {
        var jwt = _factory.Services.GetRequiredService<JwtTokenService>();
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.CreateAccessToken(userId, "Someone", audience, permissions: permissions));
        return client;
    }

    private HttpClient McAll() => Mc(null, Permissions.MessagesView, Permissions.MessagesSend);

    private static CheckUpDto CheckUp(Guid id, decimal systolic = 185, decimal diastolic = 95) => new(
        id, Now.AddMinutes(-10), null, HealthStatus.Normal, "x", "note", null,
        [new ReadingDto(Guid.NewGuid(), IndicatorType.BloodPressure, systolic, diastolic, HealthStatus.Normal, [])]);

    private static MessageDto Report(Guid id, Guid? checkUpId, string subject = "Dizzy after exercise") => new(
        id, MessageDirection.ToMissionControl, MessageKind.Report, checkUpId, subject, "BP high after treadmill; resting now.",
        Now.AddMinutes(-9), null, null, null);

    private async Task<SyncResponse> Sync(
        HttpClient http, IEnumerable<CheckUpDto>? checkUps = null, IEnumerable<MessageDto>? messages = null, DateTimeOffset? lastPulled = null)
    {
        var request = new SyncRequest("dev", lastPulled, "2026.11.14-1", null, (checkUps ?? []).ToList(), (messages ?? []).ToList());
        var res = await http.PostAsJsonAsync("/api/crew/v1/sync", request, Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return (await res.Content.ReadFromJsonAsync<SyncResponse>(Json))!;
    }

    private Task<Paged<MessageDto>> List(Guid? astronautId = null, string query = "")
        => McAll().GetFromJsonAsync<Paged<MessageDto>>($"/api/mc/v1/crew/{astronautId ?? _astronautId}/messages{query}", Json)!;

    [Fact]
    public async Task Endpoints_require_their_permissions()
    {
        var path = $"/api/mc/v1/crew/{_astronautId}/messages";
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(null, Permissions.MessagesSend).GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Crew().GetAsync(path)).StatusCode);

        var body = new SendMessageRequest("Hi", "Hello");
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(null, Permissions.MessagesView).PostAsJsonAsync(path, body)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Mc(null, Permissions.MessagesSend).PostAsync($"/api/mc/v1/messages/{Guid.NewGuid()}/read", null)).StatusCode);
    }

    [Fact]
    public async Task Report_pushed_by_sync_appears_in_the_portal_linked_to_its_check_up()
    {
        var checkUpId = Guid.NewGuid();
        var reportId = Guid.NewGuid();

        var sync = await Sync(Crew(), [CheckUp(checkUpId)], [Report(reportId, checkUpId)]);
        Assert.Contains(checkUpId, sync.Accepted);
        Assert.Contains(reportId, sync.Accepted);
        Assert.Empty(sync.Rejected);

        var page = await List();
        var message = Assert.Single(page.Items);
        Assert.Equal(reportId, message.Id);
        Assert.Equal(MessageKind.Report, message.Kind);
        Assert.Equal(MessageDirection.ToMissionControl, message.Direction);
        Assert.Equal(checkUpId, message.CheckUpId);
        Assert.Equal("Ana Rivera", message.SenderName);
        Assert.NotNull(message.ReceivedAt);       // stamped by the server on sync
        Assert.Null(message.ReadAt);
    }

    [Fact]
    public async Task Report_may_reference_a_check_up_synced_earlier()
    {
        var checkUpId = Guid.NewGuid();
        await Sync(Crew(), [CheckUp(checkUpId)]);

        var reportId = Guid.NewGuid();
        var sync = await Sync(Crew(), messages: [Report(reportId, checkUpId)]);

        Assert.Contains(reportId, sync.Accepted);
    }

    [Fact]
    public async Task Pushing_the_same_message_twice_stores_it_once()
    {
        var reportId = Guid.NewGuid();
        var checkUpId = Guid.NewGuid();
        await Sync(Crew(), [CheckUp(checkUpId)], [Report(reportId, checkUpId)]);
        var retry = await Sync(Crew(), [CheckUp(checkUpId)], [Report(reportId, checkUpId), Report(reportId, checkUpId)]);

        Assert.Contains(reportId, retry.Accepted);
        Assert.Equal(1, (await List()).Total);
    }

    [Fact]
    public async Task Invalid_pushed_messages_are_rejected_with_a_reason_and_the_rest_still_syncs()
    {
        var checkUpId = Guid.NewGuid();
        var foreignCheckUp = Guid.NewGuid();
        await Sync(Crew(_otherAstronautId), [CheckUp(foreignCheckUp)]);

        var good = Report(Guid.NewGuid(), checkUpId);
        var noCheckUp = Report(Guid.NewGuid(), null);
        var foreign = Report(Guid.NewGuid(), foreignCheckUp);
        var unknown = Report(Guid.NewGuid(), Guid.NewGuid());
        var blank = Report(Guid.NewGuid(), checkUpId, subject: "  ");
        var wrongWay = good with { Id = Guid.NewGuid(), Direction = MessageDirection.ToAstronaut };
        var tooLong = good with { Id = Guid.NewGuid(), Body = new string('x', 4001) };

        var sync = await Sync(Crew(), [CheckUp(checkUpId)], [good, noCheckUp, foreign, unknown, blank, wrongWay, tooLong]);

        Assert.Contains(good.Id, sync.Accepted);
        Assert.Equal(
            new[] { noCheckUp.Id, foreign.Id, unknown.Id, blank.Id, wrongWay.Id, tooLong.Id }.Order(),
            sync.Rejected.Select(r => r.Id).Order());
        Assert.All(sync.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        Assert.Equal(1, (await List()).Total);
    }

    [Fact]
    public async Task A_message_id_used_by_another_astronaut_is_rejected_and_the_astronaut_comes_from_the_token()
    {
        var checkUpId = Guid.NewGuid();
        var id = Guid.NewGuid();
        await Sync(Crew(_otherAstronautId), [CheckUp(checkUpId)], [Report(id, checkUpId)]);

        var mine = Guid.NewGuid();
        var sync = await Sync(Crew(), [CheckUp(mine)], [Report(id, mine)]);

        Assert.Contains(sync.Rejected, r => r.Id == id);
        Assert.Equal(1, (await List(_otherAstronautId)).Total);
        Assert.Equal(0, (await List()).Total);
    }

    [Fact]
    public async Task Marking_read_is_idempotent_and_only_applies_to_astronaut_messages()
    {
        var checkUpId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await Sync(Crew(), [CheckUp(checkUpId)], [Report(reportId, checkUpId)]);
        var http = McAll();

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/mc/v1/messages/{reportId}/read", null)).StatusCode);
        var readAt = (await List()).Items.Single().ReadAt;
        Assert.True(readAt >= before);

        Assert.Equal(HttpStatusCode.NoContent, (await http.PostAsync($"/api/mc/v1/messages/{reportId}/read", null)).StatusCode);
        Assert.Equal(readAt, (await List()).Items.Single().ReadAt);   // not overwritten

        Assert.Equal(HttpStatusCode.NotFound, (await http.PostAsync($"/api/mc/v1/messages/{Guid.NewGuid()}/read", null)).StatusCode);

        var reply = await (await http.PostAsJsonAsync($"/api/mc/v1/crew/{_astronautId}/messages", new SendMessageRequest("Re", "ok"), Json))
            .Content.ReadFromJsonAsync<MessageDto>(Json);
        Assert.Equal(HttpStatusCode.Conflict, (await http.PostAsync($"/api/mc/v1/messages/{reply!.Id}/read", null)).StatusCode);
    }

    [Fact]
    public async Task Portal_reply_is_delivered_in_the_next_sync_exactly_once()
    {
        var res = await Mc(_surgeonId, Permissions.MessagesSend, Permissions.MessagesView)
            .PostAsJsonAsync($"/api/mc/v1/crew/{_astronautId}/messages", new SendMessageRequest("  Rest 24 h  ", "  Recheck tomorrow.  "), Json);
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var created = (await res.Content.ReadFromJsonAsync<MessageDto>(Json))!;
        Assert.Equal((MessageDirection.ToAstronaut, MessageKind.Message), (created.Direction, created.Kind));
        Assert.Equal(("Rest 24 h", "Recheck tomorrow."), (created.Subject, created.Body));
        Assert.Equal("Dr. Okafor", created.SenderName);
        Assert.Null(created.ReceivedAt);   // waiting for the astronaut's next sync

        var first = await Sync(Crew());
        var delivered = Assert.Single(first.Inbound.Messages);
        Assert.Equal(created.Id, delivered.Id);
        Assert.Equal("Dr. Okafor", delivered.SenderName);
        Assert.NotNull(delivered.ReceivedAt);

        // The desktop stores serverTime as its next lastPulledAt: nothing is delivered twice.
        var second = await Sync(Crew(), lastPulled: first.ServerTime);
        Assert.Empty(second.Inbound.Messages);

        // The portal now sees when it was delivered.
        Assert.NotNull((await List()).Items.Single(m => m.Id == created.Id).ReceivedAt);
    }

    [Fact]
    public async Task A_reply_only_reaches_its_own_astronaut()
    {
        await McAll().PostAsJsonAsync($"/api/mc/v1/crew/{_astronautId}/messages", new SendMessageRequest("For Ana", "x"), Json);

        Assert.Empty((await Sync(Crew(_otherAstronautId))).Inbound.Messages);
        Assert.Single((await Sync(Crew())).Inbound.Messages);
    }

    [Fact]
    public async Task Send_validates_the_body_the_astronaut_and_the_sender()
    {
        var path = $"/api/mc/v1/crew/{_astronautId}/messages";
        var http = McAll();

        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(path, new SendMessageRequest("  ", "body"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(path, new SendMessageRequest("Subject", ""), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(path, new SendMessageRequest(new string('s', 121), "b"), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsJsonAsync(path, new SendMessageRequest("s", new string('b', 4001)), Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await http.PostAsJsonAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/messages", new SendMessageRequest("s", "b"), Json)).StatusCode);

        // A token whose portal user does not exist (or is disabled) cannot send.
        var ghost = Mc(Guid.NewGuid(), Permissions.MessagesSend);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ghost.PostAsJsonAsync(path, new SendMessageRequest("s", "b"), Json)).StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.PortalUsers.Where(u => u.Id == _surgeonId).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        }

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await Mc(_surgeonId, Permissions.MessagesSend).PostAsJsonAsync(path, new SendMessageRequest("s", "b"), Json)).StatusCode);
    }

    [Fact]
    public async Task List_is_newest_first_paged_and_per_astronaut()
    {
        var http = McAll();
        for (var i = 1; i <= 3; i++)
        {
            await http.PostAsJsonAsync($"/api/mc/v1/crew/{_astronautId}/messages", new SendMessageRequest($"m{i}", "b"), Json);
            await Task.Delay(5);
        }

        await http.PostAsJsonAsync($"/api/mc/v1/crew/{_otherAstronautId}/messages", new SendMessageRequest("other", "b"), Json);

        var all = await List();
        Assert.Equal(3, all.Total);
        Assert.Equal(["m3", "m2", "m1"], all.Items.Select(m => m.Subject));

        var page2 = await List(query: "?page=2&pageSize=2");
        Assert.Equal((2, 2, 3), (page2.Page, page2.PageSize, page2.Total));
        Assert.Equal(["m1"], page2.Items.Select(m => m.Subject));

        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync($"/api/mc/v1/crew/{_astronautId}/messages?pageSize=0")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync($"/api/mc/v1/crew/{Guid.NewGuid()}/messages")).StatusCode);
    }

    [Fact]
    public async Task Unread_reports_feed_the_crew_overview_counter()
    {
        var checkUpId = Guid.NewGuid();
        var reportId = Guid.NewGuid();
        await Sync(Crew(), [CheckUp(checkUpId)], [Report(reportId, checkUpId)]);

        var http = Mc(null, Permissions.MessagesView, Permissions.CrewView);
        async Task<int> Unread() => (await http.GetFromJsonAsync<List<CrewSummaryDto>>("/api/mc/v1/crew", Json))!
            .Single(c => c.AstronautId == _astronautId).UnreadMessages;

        Assert.Equal(1, await Unread());
        await http.PostAsync($"/api/mc/v1/messages/{reportId}/read", null);
        Assert.Equal(0, await Unread());
    }
}
