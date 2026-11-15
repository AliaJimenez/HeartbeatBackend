using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Heartbeat.Api.Auth;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Data.Seeding;
using Heartbeat.Api.Deployment;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Heartbeat.Tests;

/// <summary>SEED (#10): the three stories of CONTRACTS.md §7, evaluated with the shared evaluator.</summary>
public class DemoDataSeederTests : IClassFixture<DemoDataSeederTests.Factory>
{
    public sealed class Factory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");

        // Development environment: the demo passwords come from appsettings.Development.json, as the ticket requires.
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

    private readonly Factory _factory;

    public DemoDataSeederTests(Factory factory) => _factory = factory;

    private async Task<T> Db<T>(Func<AppDbContext, Task<T>> query)
    {
        using var scope = _factory.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    private async Task<List<CheckUp>> CheckUps(string userName)
        => (await Db(db => db.CheckUps.Include(c => c.Readings).Where(c => c.Astronaut.UserName == userName).ToListAsync()))
            .OrderBy(c => c.RecordedAt).ToList();

    private static decimal Value(CheckUp c, IndicatorType i) => c.Readings.Single(r => r.Indicator == i).Value;

    private string Password(string user) => _factory.Services.GetRequiredService<IConfiguration>()[$"Seed:Passwords:{user}"]!;

    [Fact]
    public async Task Creates_the_mission_three_astronauts_and_thirty_daily_check_ups_each()
    {
        var mission = await Db(db => db.Missions.SingleAsync(m => m.Name == DemoDataSeeder.MissionName));
        Assert.Equal("Artemis Demo 1", mission.Name);

        foreach (var user in new[] { "a.rivera", "k.sato", "l.haddad" })
        {
            var list = await CheckUps(user);
            Assert.Equal(30, list.Count);
            Assert.All(list, c => Assert.Equal(6, c.Readings.Count));

            // One per day, ending now (the 30 days are anchored at seed time and never in the future).
            Assert.Equal(30, list.Select(c => DateOnly.FromDateTime(c.RecordedAt.UtcDateTime)).Distinct().Count());
            Assert.True(list[^1].RecordedAt <= DateTimeOffset.UtcNow);
            Assert.True(list[^1].RecordedAt > DateTimeOffset.UtcNow.AddDays(-2));
            Assert.All(list, c => Assert.True(c.ReceivedAt <= DateTimeOffset.UtcNow));
        }

        var astronauts = await Db(db => db.Astronauts.OrderBy(a => a.UserName).ToListAsync());
        Assert.Equal(["Comet", "Atlas", "Nova"], astronauts.OrderBy(a => a.UserName == "a.rivera" ? 0 : a.UserName == "k.sato" ? 1 : 2).Select(a => a.CallSign));
        Assert.All(astronauts, a => Assert.Equal(mission.Id, a.MissionId));
    }

    [Fact]
    public async Task Portal_users_have_the_three_roles_with_the_permissions_of_the_contract()
    {
        var users = await Db(db => db.PortalUsers.Include(u => u.Role).ThenInclude(r => r.Permissions).ToListAsync());

        Assert.Equal(3, users.Count);
        Assert.Equal(RoleNames.Administrator, users.Single(u => u.UserName == "admin").Role.Name);
        Assert.Equal(RoleNames.FlightSurgeon, users.Single(u => u.UserName == "j.okafor").Role.Name);
        Assert.Equal(RoleNames.MissionControlOperator, users.Single(u => u.UserName == "m.chen").Role.Name);
        Assert.All(users, u => Assert.True(u.Role.IsSystem));

        string[] Perms(string user) => users.Single(u => u.UserName == user).Role.Permissions.Select(p => p.Permission).Order().ToArray();
        Assert.Equal(Permissions.All.Order(), Perms("admin"));
        Assert.Equal(
            [Permissions.AlertsAcknowledge, Permissions.AlertsView, Permissions.CrewView, Permissions.MessagesSend,
             Permissions.MessagesView, Permissions.VitalsHistory, Permissions.VitalsView],
            Perms("j.okafor"));
        Assert.Equal(
            [Permissions.AlertsView, Permissions.CrewView, Permissions.MessagesSend, Permissions.MessagesView, Permissions.VitalsView],
            Perms("m.chen"));
    }

    [Fact]
    public async Task Every_stored_status_and_band_comes_from_the_shared_evaluator()
    {
        var evaluator = new HealthEvaluator();
        foreach (var user in new[] { "a.rivera", "k.sato", "l.haddad" })
        {
            foreach (var checkUp in await CheckUps(user))
            {
                var inputs = checkUp.Readings.Select(r => new ReadingInput(r.Indicator, r.Value, r.SecondaryValue)).ToList();
                var expected = evaluator.Evaluate(inputs);

                Assert.Equal(expected.OverallStatus, checkUp.OverallStatus);
                Assert.Equal(evaluator.RangesVersion, checkUp.RangesVersion);
                foreach (var (reading, exp) in checkUp.Readings.Zip(expected.Readings))
                {
                    Assert.Equal(exp.Status, reading.Status);
                    Assert.Equal(string.Join(",", exp.BandIds), reading.BandIds);
                }
            }
        }
    }

    [Fact]
    public async Task Ana_is_healthy_then_spikes_to_critical_on_day_30_with_an_open_alert_and_a_report()
    {
        var list = await CheckUps("a.rivera");
        Assert.All(list.Take(29), c => Assert.Equal(HealthStatus.Normal, c.OverallStatus));

        var spike = list[^1];
        Assert.Equal(HealthStatus.Critical, spike.OverallStatus);
        Assert.Equal((185m, 95m), (Value(spike, IndicatorType.BloodPressure), spike.Readings.Single(r => r.Indicator == IndicatorType.BloodPressure).SecondaryValue));

        var alert = await Db(db => db.Alerts.SingleAsync(a => a.Astronaut.UserName == "a.rivera" && a.Kind == AlertKind.CheckUp));
        Assert.Equal(spike.Id, alert.CheckUpId);
        Assert.Equal("BloodPressure", alert.Indicators);
        Assert.Null(alert.AcknowledgedAt);

        var report = await Db(db => db.Messages.SingleAsync(m => m.Astronaut.UserName == "a.rivera"));
        Assert.Equal(MessageKind.Report, report.Kind);
        Assert.Equal(MessageDirection.ToMissionControl, report.Direction);
        Assert.Equal(spike.Id, report.CheckUpId);
        Assert.Null(report.ReadAt);
    }

    [Fact]
    public async Task Kenji_sleep_slides_from_7_to_4_point_5_hours_and_mood_drops_to_2_without_critical_results()
    {
        var list = await CheckUps("k.sato");

        Assert.InRange(Value(list[0], IndicatorType.Sleep), 6.8m, 7.2m);
        Assert.Equal(4.5m, Value(list[^1], IndicatorType.Sleep));
        Assert.Equal(2m, Value(list[^1], IndicatorType.Mood));
        Assert.Equal(HealthStatus.Watch, list[^1].OverallStatus);

        // Monotone slide over the last two weeks.
        var slide = list.Skip(16).Select(c => Value(c, IndicatorType.Sleep)).ToList();
        Assert.Equal(slide.OrderByDescending(v => v), slide);
        Assert.True(list.Take(16).All(c => c.OverallStatus == HealthStatus.Normal));

        Assert.DoesNotContain(list, c => c.OverallStatus == HealthStatus.Critical);
        Assert.Equal(0, await Db(db => db.Alerts.CountAsync(a => a.Astronaut.UserName == "k.sato" && a.Kind == AlertKind.CheckUp)));
    }

    [Fact]
    public async Task Leila_has_a_fever_on_days_18_to_20_and_an_acknowledged_alert()
    {
        var list = await CheckUps("l.haddad");
        var temps = list.Select(c => Value(c, IndicatorType.BodyTemperature)).ToList();

        Assert.Equal([38.4m, 38.8m, 39.2m], temps.Skip(17).Take(3));
        Assert.Equal(HealthStatus.Watch, list[17].OverallStatus);
        Assert.Equal(HealthStatus.Critical, list[19].OverallStatus);
        Assert.All(list.Take(17), c => Assert.Equal(HealthStatus.Normal, c.OverallStatus));
        Assert.All(list.Skip(22), c => Assert.Equal(HealthStatus.Normal, c.OverallStatus));   // recovered

        var alert = await Db(db => db.Alerts.SingleAsync(a => a.Astronaut.UserName == "l.haddad" && a.Kind == AlertKind.CheckUp));
        Assert.Equal(list[19].Id, alert.CheckUpId);
        Assert.NotNull(alert.AcknowledgedAt);
        Assert.Equal("Rest 24h, recheck", alert.AcknowledgeNote);
        var okafor = await Db(db => db.PortalUsers.SingleAsync(u => u.UserName == "j.okafor"));
        Assert.Equal(okafor.Id, alert.AcknowledgedByUserId);
    }

    [Fact]
    public async Task Seeding_twice_changes_nothing()
    {
        async Task<int[]> Counts() => await Db<int[]>(async db =>
        [
            await db.Roles.CountAsync(), await db.PortalUsers.CountAsync(), await db.Astronauts.CountAsync(),
            await db.CheckUps.CountAsync(), await db.Readings.CountAsync(), await db.Alerts.CountAsync(),
            await db.Messages.CountAsync(), await db.Users.CountAsync(),
        ]);

        var before = await Counts();
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDataSeeder>().SeedAsync(scope.ServiceProvider, default);
        }

        Assert.Equal(before, await Counts());
    }

    [Fact]
    public async Task Demo_accounts_can_sign_in_with_the_passwords_from_development_settings()
    {
        var http = _factory.CreateClient();
        foreach (var user in new[] { "admin", "j.okafor", "m.chen" })
        {
            var res = await http.PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest(user, Password(user), "t"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        foreach (var user in new[] { "a.rivera", "k.sato", "l.haddad" })
        {
            var res = await http.PostAsJsonAsync("/api/crew/v1/auth/login", new LoginRequest(user, Password(user), "t"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
    }

    [Fact]
    public async Task Crew_overview_shows_the_three_stories()
    {
        var login = await (await _factory.CreateClient().PostAsJsonAsync("/api/mc/v1/auth/login", new LoginRequest("admin", Password("admin"), "t")))
            .Content.ReadFromJsonAsync<McLoginResponse>(Json);
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.AccessToken);

        var crew = (await http.GetFromJsonAsync<List<CrewSummaryDto>>("/api/mc/v1/crew", Json))!;
        var ana = crew.Single(c => c.DisplayName == "Ana Rivera");
        var kenji = crew.Single(c => c.DisplayName == "Kenji Sato");
        var leila = crew.Single(c => c.DisplayName == "Leila Haddad");

        Assert.Equal((HealthStatus.Critical, 1, 1), (ana.OverallStatus, ana.OpenAlerts, ana.UnreadMessages));
        Assert.Equal((HealthStatus.Watch, 0, 0), (kenji.OverallStatus, kenji.OpenAlerts, kenji.UnreadMessages));
        Assert.Equal((HealthStatus.Normal, 0, 0), (leila.OverallStatus, leila.OpenAlerts, leila.UnreadMessages));
    }

    [Fact]
    public void Demo_passwords_are_defined_only_in_development_settings()
    {
        var apiDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Heartbeat.Api"));
        foreach (var file in Directory.GetFiles(apiDir, "appsettings*.json"))
        {
            var text = File.ReadAllText(file);
            var hasPasswords = text.Contains("\"Passwords\"", StringComparison.Ordinal);
            Assert.Equal(Path.GetFileName(file) == "appsettings.Development.json", hasPasswords);
        }
    }

    [Fact]
    public void Seed_stops_without_a_configured_password()
    {
        var seeder = new DemoDataSeeder(new ConfigurationBuilder().Build(), Microsoft.Extensions.Logging.Abstractions.NullLogger<DemoDataSeeder>.Instance);
        // Fresh database so there is something to create.
        var path = Path.Combine(Path.GetTempPath(), $"hb-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={path}"));
        services.AddIdentity<ApplicationUser, Microsoft.AspNetCore.Identity.IdentityRole<Guid>>().AddEntityFrameworkStores<AppDbContext>();
        using var provider = services.BuildServiceProvider();
        using var inner = provider.CreateScope();
        inner.ServiceProvider.GetRequiredService<AppDbContext>().Database.Migrate();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => seeder.SeedAsync(inner.ServiceProvider, default));
        Assert.Contains("Seed:Passwords:admin", ex.GetAwaiter().GetResult().Message);

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { File.Delete(path); } catch (IOException) { }
    }
}
