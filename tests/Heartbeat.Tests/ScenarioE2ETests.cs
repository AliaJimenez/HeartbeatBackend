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
/// HB-QA-01 (#126): the improvements end to end on the demo seed, through the real HTTP pipeline, the real desktop
/// client and real logins. A solar particle event, a high-CO2 reading and a sleep-deprived check-up are recorded on the
/// desktop while the Earth link is down, evaluated locally, pushed when the link is back, and Mission Control sees
/// them (alerts, risk traffic light per hazard, radiation, environment, mission) and answers.
/// The desktop and portal screens themselves live in other repositories; see docs/QA-E2E.md for their checklist.
/// </summary>
public class ScenarioE2ETests : IDisposable
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

    /// <summary>The Earth link: while it is down every call fails like a dead network would.</summary>
    private sealed class EarthLink : DelegatingHandler
    {
        public bool Up { get; set; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Up ? base.SendAsync(request, cancellationToken) : throw new HttpRequestException("No link with Earth (simulated)");
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string DeviceId = "qa-desktop";
    private const string RangesVersion = "2026.11.14-1";

    private readonly Factory _factory = new();

    public void Dispose() => _factory.Dispose();

    private string Password(string user) => _factory.Services.GetRequiredService<IConfiguration>()[$"Seed:Passwords:{user}"]!;

    private async Task<HttpClient> PortalAsync(string user)
    {
        var http = _factory.CreateClient();
        var res = await http.PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest(user, Password(user), "portal"), Json);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var login = (await res.Content.ReadFromJsonAsync<McLoginResponse>(Json))!;
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return http;
    }

    private static Task<T> Get<T>(HttpClient http, string path) => http.GetFromJsonAsync<T>(path, Json)!;

    private async Task<Guid> IdOf(HttpClient portal, string displayName)
        => (await Get<List<CrewSummaryDto>>(portal, "/api/mc/v1/crew")).Single(c => c.DisplayName.Contains(displayName)).AstronautId;

    // ----- Desktop side: what the desktop keeps and computes without a network -----

    private sealed class OfflineQueue
    {
        public List<CheckUpDto> CheckUps { get; } = [];
        public List<RadiationDoseDto> Doses { get; } = [];
        public List<EnvironmentReading> Environment { get; } = [];
        public List<MessageDto> Messages { get; } = [];
        public DateTimeOffset? LastPulledAt { get; set; }

        public SyncRequest ToRequest() => new(
            DeviceId, LastPulledAt, RangesVersion, null, CheckUps.ToList(), Messages.ToList(), Doses.ToList(), Environment.ToList());

        public void Clear()
        {
            CheckUps.Clear();
            Doses.Clear();
            Environment.Clear();
            Messages.Clear();
        }
    }

    /// <summary>A check-up as the desktop builds it: the shared evaluator runs locally, no network needed.</summary>
    private static CheckUpDto RecordLocally(DateTimeOffset at, params ReadingInput[] inputs)
    {
        var evaluation = new HealthEvaluator().Evaluate(inputs);
        var readings = evaluation.Readings
            .Select((r, i) => new ReadingDto(Guid.CreateVersion7(), r.Indicator, inputs[i].Value, inputs[i].SecondaryValue, r.Status, r.BandIds))
            .ToList();
        return new CheckUpDto(Guid.CreateVersion7(), at, null, evaluation.OverallStatus, evaluation.RangesVersion, "Slept badly, ears ringing", null, readings);
    }

    private static ReadingInput[] SleepDeprived() =>
    [
        new(IndicatorType.BloodPressure, 118, 76),
        new(IndicatorType.HeartRate, 62),
        new(IndicatorType.BloodSugar, 90),
        new(IndicatorType.BodyTemperature, 36.6m),
        new(IndicatorType.Sleep, 3.2m),
        new(IndicatorType.Mood, 2),
    ];

    private async Task<(CrewApiClient Desktop, EarthLink Link, Guid AstronautId)> DesktopAsync(string user)
    {
        var link = new EarthLink();
        var desktop = new CrewApiClient(_factory.CreateDefaultClient(link));
        var login = await desktop.LoginAsync(new LoginRequest(user, Password(user), DeviceId));
        return (desktop, link, login.Profile.Id);
    }

    // ----- 1. The seed tells the story on the portal -----

    [Fact]
    public async Task The_seed_shows_the_three_stories_to_mission_control()
    {
        var portal = await PortalAsync("j.okafor");
        var crew = await Get<List<CrewSummaryDto>>(portal, "/api/mc/v1/crew");
        var ana = crew.Single(c => c.DisplayName.Contains("Ana"));
        var kenji = crew.Single(c => c.DisplayName.Contains("Kenji"));
        var leila = crew.Single(c => c.DisplayName.Contains("Leila"));

        // Ana: blood-pressure spike, open alert and an unread report.
        Assert.Equal(HealthStatus.Critical, ana.OverallStatus);
        Assert.Equal(1, ana.OpenAlerts);
        Assert.Equal(1, ana.UnreadMessages);
        var anaRisk = await Get<RiskStatus>(portal, $"/api/mc/v1/crew/{ana.AstronautId}/risk");
        Assert.Equal(HealthStatus.Critical, anaRisk.Status);
        Assert.False(string.IsNullOrWhiteSpace(anaRisk.Explanation));
        Assert.Equal(5, anaRisk.Hazards.Count);

        // Kenji: sleep slides down over two weeks, mood falls (watch, never critical).
        var kenjiRisk = await Get<RiskStatus>(portal, $"/api/mc/v1/crew/{kenji.AstronautId}/risk");
        Assert.Equal(HealthStatus.Watch, kenjiRisk.Status);
        Assert.Equal(HealthStatus.Watch, kenjiRisk.Hazards.Single(h => h.Hazard == Hazard.IsolationAndConfinement).Status);
        var sleep = await Get<SeriesDto>(portal, $"/api/mc/v1/crew/{kenji.AstronautId}/series?indicator=sleep");
        Assert.True(sleep.Points[^1].Value < sleep.Points[0].Value - 1m, "Kenji's sleep should have slid down");

        // Leila: a solar particle event 10 days ago, now in the watch band of the radiation hazard.
        var leilaRisk = await Get<RiskStatus>(portal, $"/api/mc/v1/crew/{leila.AstronautId}/risk");
        Assert.Equal(HealthStatus.Watch, leilaRisk.Hazards.Single(h => h.Hazard == Hazard.SpaceRadiation).Status);
        var radiation = await Get<RadiationSummaryDto>(portal, $"/api/mc/v1/crew/{leila.AstronautId}/radiation");
        Assert.Equal(180m, radiation.SpeDoseMsv);
        Assert.Contains(radiation.Doses, d => d.Type == RadiationType.Spe);

        // The habitat: a CO2 spike in the history, back to normal today. Simulated and labelled so.
        foreach (var member in crew)
        {
            var environment = await Get<EnvironmentSummaryDto>(portal, $"/api/mc/v1/crew/{member.AstronautId}/environment?indicator=co2&pageSize=100");
            Assert.True(environment.Simulated);
            Assert.Equal(HealthStatus.Normal, environment.Status);
            Assert.Contains(environment.History.Items, r => r.Status == HealthStatus.Critical && r.Value >= 5m);
        }

        // The mission view.
        var mission = await Get<MissionProfileDto>(portal, "/api/mc/v1/mission");
        Assert.Equal("station", mission.EnvironmentProfile.Type);
        Assert.True(mission.CommunicationDelaySeconds > 0);
        Assert.NotNull(mission.Indices);
        Assert.True(mission.Indices!.Simulated);
    }

    [Fact]
    public async Task The_seeded_events_have_the_alerts_the_sync_would_have_raised()
    {
        var portal = await PortalAsync("j.okafor");
        var leila = await IdOf(portal, "Leila");
        var alerts = (await Get<Paged<AlertDto>>(portal, "/api/mc/v1/alerts?state=all&pageSize=100")).Items.Where(a => a.AstronautId == leila).ToList();

        Assert.Contains(alerts, a => a.Kind == AlertKind.CheckUp);
        Assert.Contains(alerts, a => a.Kind == AlertKind.Radiation && a.RadiationDoseId is not null);
        Assert.Contains(alerts, a => a.Kind == AlertKind.Environment && (a.Message ?? "").Contains("critical"));
        Assert.All(alerts, a => Assert.NotNull(a.AcknowledgedAt));   // all happened 10 days ago and were handled
    }

    [Fact]
    public async Task Seeded_environment_readings_are_rounded_for_display()
    {
        var portal = await PortalAsync("j.okafor");
        var ana = await IdOf(portal, "Ana");

        var environment = await Get<EnvironmentSummaryDto>(portal, $"/api/mc/v1/crew/{ana}/environment");

        Assert.All(environment.Latest, r => Assert.True(decimal.Round(r.Value, 2) == r.Value, $"{r.IndicatorId} = {r.Value}"));
    }

    // ----- 2. The scenario: SPE + high CO2 + sleep deprivation, offline, then sync -----

    [Fact]
    public async Task Spe_high_co2_and_sleep_deprivation_recorded_offline_reach_mission_control_when_the_link_returns()
    {
        var (desktop, link, kenjiId) = await DesktopAsync("k.sato");
        using var _ = desktop;
        var portal = await PortalAsync("j.okafor");
        var queue = new OfflineQueue();
        var now = DateTimeOffset.UtcNow;

        // The Earth link goes down. The desktop keeps working: everything is evaluated locally with the shared code.
        link.Up = false;
        var spe = new RadiationDoseDto(Guid.CreateVersion7(), now.AddMinutes(-30), null, 140m, "Solar particle event during EVA", RadiationType.Spe);
        var co2 = new EnvironmentReading(Guid.CreateVersion7(), now.AddMinutes(-20), null, "co2", 5.8m, Note: "Scrubber fault");
        var checkUp = RecordLocally(now.AddMinutes(-5), SleepDeprived());
        queue.Doses.Add(spe);
        queue.Environment.Add(co2);
        queue.CheckUps.Add(checkUp);

        Assert.Equal(HealthStatus.Critical, checkUp.OverallStatus);
        Assert.Equal(HealthStatus.Critical, IndicatorsCatalog.Evaluate(IndicatorsCatalog.Find("co2")!, co2.Value).Status);
        Assert.Equal(HealthStatus.Watch, RadiationDoses.StatusForSpePercent(RadiationDoses.PercentOfSpeLimit(spe.DoseMsv)));

        // Syncing without a link fails and loses nothing: the queue is untouched and can be retried as is.
        await Assert.ThrowsAsync<HttpRequestException>(() => desktop.SyncAsync(queue.ToRequest()));
        Assert.Equal((1, 1, 1), (queue.CheckUps.Count, queue.Doses.Count, queue.Environment.Count));

        // Mission Control sees nothing of it yet.
        Assert.Empty((await Get<Paged<AlertDto>>(portal, "/api/mc/v1/alerts?pageSize=100")).Items.Where(a => a.AstronautId == kenjiId));
        Assert.Equal(HealthStatus.Watch, (await Get<RiskStatus>(portal, $"/api/mc/v1/crew/{kenjiId}/risk")).Status);

        // The link is back: one sync pushes everything.
        link.Up = true;
        var first = await desktop.SyncAsync(queue.ToRequest());
        Assert.Empty(first.Rejected);
        Assert.Equivalent(new[] { checkUp.Id, spe.Id, co2.Id }, first.Accepted);
        Assert.True(first.RadiationTotalMsv >= 154m);   // the 14 mSv of background in the seed plus the SPE

        // A retry of the same queue (the desktop did not get the answer) changes nothing.
        var retry = await desktop.SyncAsync(queue.ToRequest());
        Assert.Empty(retry.Rejected);
        Assert.Equivalent(new[] { checkUp.Id, spe.Id, co2.Id }, retry.Accepted);
        queue.Clear();
        queue.LastPulledAt = first.ServerTime;

        // Portal, alerts: one open alert of each kind for Kenji, none duplicated.
        var open = (await Get<Paged<AlertDto>>(portal, "/api/mc/v1/alerts?pageSize=100")).Items.Where(a => a.AstronautId == kenjiId).ToList();
        Assert.Equivalent(new[] { AlertKind.CheckUp, AlertKind.Radiation, AlertKind.Environment }, open.Select(a => a.Kind));
        Assert.Equal(checkUp.Id, open.Single(a => a.Kind == AlertKind.CheckUp).CheckUpId);
        Assert.Equal(spe.Id, open.Single(a => a.Kind == AlertKind.Radiation).RadiationDoseId);
        Assert.Contains("(simulated)", open.Single(a => a.Kind == AlertKind.Environment).Message!);

        // Portal, mission view: crew card, risk traffic light per hazard, radiation, environment.
        var card = (await Get<List<CrewSummaryDto>>(portal, "/api/mc/v1/crew")).Single(c => c.AstronautId == kenjiId);
        Assert.Equal(HealthStatus.Critical, card.OverallStatus);
        Assert.Equal(3, card.OpenAlerts);

        var risk = await Get<RiskStatus>(portal, $"/api/mc/v1/crew/{kenjiId}/risk");
        Assert.Equal(HealthStatus.Critical, risk.Status);
        Assert.Equal(HealthStatus.Watch, risk.Hazards.Single(h => h.Hazard == Hazard.SpaceRadiation).Status);
        Assert.Equal(HealthStatus.Critical, risk.Hazards.Single(h => h.Hazard == Hazard.IsolationAndConfinement).Status);
        Assert.Equal(HealthStatus.Critical, risk.Hazards.Single(h => h.Hazard == Hazard.HostileClosedEnvironment).Status);
        Assert.All(risk.Hazards, h => Assert.False(string.IsNullOrWhiteSpace(h.Explanation)));

        var radiation = await Get<RadiationSummaryDto>(portal, $"/api/mc/v1/crew/{kenjiId}/radiation");
        Assert.Equal(140m, radiation.SpeDoseMsv);
        Assert.Equal(HealthStatus.Watch, radiation.Status);

        var environment = await Get<EnvironmentSummaryDto>(portal, $"/api/mc/v1/crew/{kenjiId}/environment");
        Assert.Equal(HealthStatus.Critical, environment.Status);
        Assert.Equal(5.8m, environment.Latest.Single(r => r.IndicatorId == "co2").Value);
        Assert.True(environment.Simulated);

        var sleep = await Get<SeriesDto>(portal, $"/api/mc/v1/crew/{kenjiId}/series?indicator=sleep");
        Assert.Equal(3.2m, sleep.Points[^1].Value);

        // What the desktop computed offline is what the server says: the shared code gives the same traffic lights.
        var local = RiskEngine.Evaluate(new RiskInputs(
            new HealthEvaluator().Evaluate(SleepDeprived()),
            Heartbeat.Api.Crew.RadiationSummaries.Build([spe], "station"),
            [Heartbeat.Api.Crew.EnvironmentReadings.ToStatus(new Heartbeat.Api.Data.Entities.EnvironmentReading
            {
                Id = co2.Id, IndicatorId = "co2", Value = co2.Value, RecordedAt = co2.RecordedAt, Simulated = true,
            })!]));
        foreach (var hazard in new[] { Hazard.SpaceRadiation, Hazard.IsolationAndConfinement, Hazard.HostileClosedEnvironment })
        {
            Assert.Equal(
                local.Hazards.Single(h => h.Hazard == hazard).Status,
                risk.Hazards.Single(h => h.Hazard == hazard).Status);
        }

        // Mission Control acknowledges all three and writes to the astronaut.
        foreach (var alert in open)
        {
            var ack = await portal.PostAsJsonAsync($"/api/mc/v1/alerts/{alert.Id}/acknowledge", new AcknowledgeAlertRequest($"Seen: {alert.Kind}"), Json);
            Assert.True(ack.IsSuccessStatusCode, await ack.Content.ReadAsStringAsync());
        }

        var reply = await portal.PostAsJsonAsync(
            $"/api/mc/v1/crew/{kenjiId}/messages", new SendMessageRequest("Shelter and rest", "Move to the storm shelter, then sleep 8 hours."), Json);
        Assert.Equal(HttpStatusCode.Created, reply.StatusCode);
        Assert.Equal(0, (await Get<List<CrewSummaryDto>>(portal, "/api/mc/v1/crew")).Single(c => c.AstronautId == kenjiId).OpenAlerts);

        // The next sync brings the answers back to the desktop.
        var pull = await desktop.SyncAsync(queue.ToRequest());
        Assert.Single(pull.Inbound.Messages);
        Assert.Equal("Shelter and rest", pull.Inbound.Messages[0].Subject);
        Assert.Equal(3, pull.Inbound.AcknowledgedAlerts.Count);
        Assert.Contains(pull.Inbound.AcknowledgedAlerts, a => a.CheckUpId == checkUp.Id);
        Assert.Contains(pull.Inbound.AcknowledgedAlerts, a => a.RadiationDoseId == spe.Id);
        Assert.Contains(pull.Inbound.AcknowledgedAlerts, a => a.CheckUpId is null && a.RadiationDoseId is null);   // the CO2 one
        Assert.All(pull.Inbound.AcknowledgedAlerts, a => Assert.Equal("Dr. J. Okafor", a.By));
    }

    // ----- 3. Offline robustness -----

    [Fact]
    public async Task An_access_token_that_expired_while_the_link_was_down_is_refreshed_on_reconnect()
    {
        var (desktop, _, _) = await DesktopAsync("k.sato");
        using var _ = desktop;
        var jwt = _factory.Services.GetRequiredService<Heartbeat.Api.Auth.JwtTokenService>();
        var astronautId = (await desktop.GetMeAsync()).Id;
        var expired = jwt.CreateAccessToken(astronautId, "Kenji Sato", Heartbeat.Api.Auth.AuthConstants.CrewAudience, lifetime: TimeSpan.FromHours(-2));
        desktop.SetTokens(expired, desktop.RefreshToken!, DeviceId);

        var queue = new OfflineQueue();
        queue.Doses.Add(new RadiationDoseDto(Guid.CreateVersion7(), DateTimeOffset.UtcNow.AddHours(-3), null, 0.5m, "GCR background", RadiationType.Gcr));
        var response = await desktop.SyncAsync(queue.ToRequest());

        Assert.Empty(response.Rejected);
        Assert.Single(response.Accepted);
        Assert.NotEqual(expired, desktop.AccessToken);
    }

    [Fact]
    public async Task A_bad_item_in_the_queue_is_rejected_with_a_reason_and_does_not_block_the_rest()
    {
        var (desktop, _, kenjiId) = await DesktopAsync("k.sato");
        using var _ = desktop;
        var portal = await PortalAsync("j.okafor");
        var now = DateTimeOffset.UtcNow;
        var queue = new OfflineQueue();
        var spe = new RadiationDoseDto(Guid.CreateVersion7(), now.AddMinutes(-30), null, 140m, "Solar particle event", RadiationType.Spe);
        var badDose = new RadiationDoseDto(Guid.CreateVersion7(), now.AddMinutes(-25), null, 0m, "Sensor glitch", RadiationType.Gcr);
        var badReading = new EnvironmentReading(Guid.CreateVersion7(), now.AddMinutes(-20), null, "cholesterol", 180m);
        var okReading = new EnvironmentReading(Guid.CreateVersion7(), now.AddMinutes(-15), null, "noise", 48m);
        queue.Doses.AddRange([spe, badDose]);
        queue.Environment.AddRange([badReading, okReading]);

        var response = await desktop.SyncAsync(queue.ToRequest());

        Assert.Equivalent(new[] { spe.Id, okReading.Id }, response.Accepted);
        Assert.Equivalent(new[] { badDose.Id, badReading.Id }, response.Rejected.Select(r => r.Id));
        Assert.All(response.Rejected, r => Assert.False(string.IsNullOrWhiteSpace(r.Reason)));
        var alerts = (await Get<Paged<AlertDto>>(portal, "/api/mc/v1/alerts?pageSize=100")).Items.Where(a => a.AstronautId == kenjiId).ToList();
        Assert.Equal(spe.Id, Assert.Single(alerts).RadiationDoseId);
    }
}