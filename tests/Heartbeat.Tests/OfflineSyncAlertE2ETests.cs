using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Client;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

/// <summary>
/// E2E (#45): the central idea of the project, end to end through the real HTTP pipeline, real login, the real
/// desktop client (<see cref="CrewApiClient"/>) and the demo data: an astronaut without network records a critical
/// check-up and a report, they sync when the network is back, Mission Control sees the alert and the report,
/// acknowledges and replies, and the astronaut receives both on the next sync.
/// </summary>
public class OfflineSyncAlertE2ETests : IDisposable
{
    private sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbPath}");
            builder.UseSetting("Seed:Enabled", "true");
        }

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

    private const string DeviceId = "e2e-laptop";
    private const string RangesVersion = "2026.11.14-1";

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string Password(string user) => _factory.Services.GetRequiredService<IConfiguration>()[$"Seed:Passwords:{user}"]!;

    /// <summary>The desktop's local queue: nothing leaves it until a sync succeeds.</summary>
    private sealed class OfflineQueue
    {
        public List<CheckUpDto> CheckUps { get; } = [];
        public List<MessageDto> Messages { get; } = [];
        public DateTimeOffset? LastPulledAt { get; set; }
    }

    private static SyncRequest Request(OfflineQueue queue)
        => new(DeviceId, queue.LastPulledAt, RangesVersion, null, queue.CheckUps.ToList(), queue.Messages.ToList());

    /// <summary>Records a check-up exactly as the desktop does: the shared evaluator runs locally, no network needed.</summary>
    private static CheckUpDto RecordLocally(DateTimeOffset at, params ReadingInput[] inputs)
    {
        var evaluation = new HealthEvaluator().Evaluate(inputs);
        var readings = evaluation.Readings
            .Select((r, i) => new ReadingDto(Guid.CreateVersion7(), r.Indicator, inputs[i].Value, inputs[i].SecondaryValue, r.Status, r.BandIds))
            .ToList();
        return new CheckUpDto(Guid.CreateVersion7(), at, null, evaluation.OverallStatus, evaluation.RangesVersion, "Felt dizzy", null, readings);
    }

    private async Task<HttpClient> McSurgeonAsync()
    {
        var http = _factory.CreateClient();
        var res = await http.PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest("j.okafor", Password("j.okafor"), "portal"), Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var login = (await res.Content.ReadFromJsonAsync<McLoginResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return http;
    }

    [Fact]
    public async Task Offline_critical_check_up_and_report_reach_the_portal_and_the_reply_comes_back_on_the_next_sync()
    {
        using var desktop = new CrewApiClient(_factory.CreateClient());
        var queue = new OfflineQueue();

        // 1. Login (needs the network once) and the profile the desktop caches.
        var login = await desktop.LoginAsync(new LoginRequest("a.rivera", Password("a.rivera"), DeviceId));
        Assert.Equal("a.rivera", login.Profile.UserName);
        var astronautId = login.Profile.Id;

        // 2. No network from here on: a critical check-up is evaluated locally and a report is queued with it.
        var critical = RecordLocally(
            DateTimeOffset.UtcNow.AddMinutes(-20),
            new ReadingInput(IndicatorType.BloodPressure, 190, 125),
            new ReadingInput(IndicatorType.HeartRate, 74));
        Assert.Equal(HealthStatus.Critical, critical.OverallStatus);
        queue.CheckUps.Add(critical);
        queue.Messages.Add(new MessageDto(
            Guid.CreateVersion7(), MessageDirection.ToMissionControl, MessageKind.Report, critical.Id,
            "Severe headache", "Headache and blurred vision since the treadmill session.",
            DateTimeOffset.UtcNow.AddMinutes(-19), null, null, null));

        var portal = await McSurgeonAsync();
        var before = (await portal.GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts", Json))!;
        Assert.DoesNotContain(before.Items, a => a.CheckUpId == critical.Id);   // nothing reaches the portal while offline

        // 3. Network back: one sync pushes everything.
        var first = await desktop.SyncAsync(Request(queue));
        Assert.Contains(critical.Id, first.Accepted);
        Assert.Contains(queue.Messages[0].Id, first.Accepted);
        Assert.Empty(first.Rejected);
        Assert.Empty(first.Inbound.Messages);
        // A first pull (no LastPulledAt) brings what was already handled before, such as the seeded CO2 alert; not this one.
        Assert.DoesNotContain(first.Inbound.AcknowledgedAlerts, a => a.CheckUpId == critical.Id);
        queue.CheckUps.Clear();
        queue.Messages.Clear();
        queue.LastPulledAt = first.ServerTime;

        // 4. The alert and the report show up in the portal.
        var open = (await portal.GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts", Json))!;
        var alert = Assert.Single(open.Items, a => a.CheckUpId == critical.Id);
        Assert.Equal(astronautId, alert.AstronautId);
        Assert.Contains(IndicatorType.BloodPressure, alert.Indicators);
        Assert.Null(alert.AcknowledgedAt);

        var inbox = (await portal.GetFromJsonAsync<Paged<MessageDto>>($"/api/mc/v1/crew/{astronautId}/messages", Json))!;
        var report = Assert.Single(inbox.Items, m => m.CheckUpId == critical.Id);
        Assert.Equal(critical.Id, report.CheckUpId);
        Assert.Equal("Severe headache", report.Subject);

        // 5. The flight surgeon acknowledges and replies.
        var ack = await portal.PostAsJsonAsync($"/api/mc/v1/alerts/{alert.Id}/acknowledge", new AcknowledgeAlertRequest("Seen, stop exercise and rest."), Json);
        Assert.True(ack.IsSuccessStatusCode, await ack.Content.ReadAsStringAsync());
        var reply = await portal.PostAsJsonAsync($"/api/mc/v1/crew/{astronautId}/messages", new SendMessageRequest("Re: Severe headache", "Rest now, recheck in 30 minutes."), Json);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);

        // 6. The astronaut sees both on the next sync, once.
        var second = await desktop.SyncAsync(Request(queue));
        var received = Assert.Single(second.Inbound.Messages);
        Assert.Equal(MessageDirection.ToAstronaut, received.Direction);
        Assert.Equal("Re: Severe headache", received.Subject);
        Assert.Equal("Dr. J. Okafor", received.SenderName);

        var acknowledged = Assert.Single(second.Inbound.AcknowledgedAlerts);
        Assert.Equal(critical.Id, acknowledged.CheckUpId);
        Assert.Equal("Seen, stop exercise and rest.", acknowledged.Note);
        Assert.Equal("Dr. J. Okafor", acknowledged.By);
        queue.LastPulledAt = second.ServerTime;

        var third = await desktop.SyncAsync(Request(queue));
        Assert.Empty(third.Inbound.Messages);
        Assert.Empty(third.Inbound.AcknowledgedAlerts);

        // 7. The portal now lists the alert as acknowledged and the report is still one conversation entry.
        var done = (await portal.GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts?state=acknowledged", Json))!;
        Assert.Contains(done.Items, a => a.Id == alert.Id && a.AcknowledgedBy == "Dr. J. Okafor");
    }

    [Fact]
    public async Task Retrying_the_same_queue_after_a_dropped_response_does_not_duplicate_the_alert_or_the_report()
    {
        using var desktop = new CrewApiClient(_factory.CreateClient());
        var login = await desktop.LoginAsync(new LoginRequest("a.rivera", Password("a.rivera"), DeviceId));

        var critical = RecordLocally(DateTimeOffset.UtcNow.AddMinutes(-5), new ReadingInput(IndicatorType.BloodPressure, 195, 130));
        var queue = new OfflineQueue();
        queue.CheckUps.Add(critical);
        queue.Messages.Add(new MessageDto(
            Guid.CreateVersion7(), MessageDirection.ToMissionControl, MessageKind.Report, critical.Id,
            "Chest pressure", "Tight chest.", DateTimeOffset.UtcNow.AddMinutes(-4), null, null, null));

        // The first response is lost on the way back, so the desktop keeps the queue and tries again.
        await desktop.SyncAsync(Request(queue));
        var retry = await desktop.SyncAsync(Request(queue));
        Assert.Empty(retry.Rejected);

        var portal = await McSurgeonAsync();
        var alerts = (await portal.GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts?state=all", Json))!;
        Assert.Single(alerts.Items, a => a.CheckUpId == critical.Id);

        var inbox = (await portal.GetFromJsonAsync<Paged<MessageDto>>($"/api/mc/v1/crew/{login.Profile.Id}/messages", Json))!;
        Assert.Single(inbox.Items, m => m.Subject == "Chest pressure");
    }

    [Fact]
    public async Task A_normal_check_up_synced_offline_raises_no_alert()
    {
        using var desktop = new CrewApiClient(_factory.CreateClient());
        await desktop.LoginAsync(new LoginRequest("a.rivera", Password("a.rivera"), DeviceId));

        var normal = RecordLocally(DateTimeOffset.UtcNow.AddMinutes(-5), new ReadingInput(IndicatorType.HeartRate, 68));
        Assert.Equal(HealthStatus.Normal, normal.OverallStatus);
        var queue = new OfflineQueue();
        queue.CheckUps.Add(normal);

        Assert.Contains(normal.Id, (await desktop.SyncAsync(Request(queue))).Accepted);

        var portal = await McSurgeonAsync();
        var alerts = (await portal.GetFromJsonAsync<Paged<AlertDto>>("/api/mc/v1/alerts?state=all", Json))!;
        Assert.DoesNotContain(alerts.Items, a => a.CheckUpId == normal.Id);
    }
}
