using Heartbeat.Api.Crew;
using Heartbeat.Api.Data;
using Heartbeat.Api.Data.Entities;
using Heartbeat.Shared;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Tests;

public class McCrewSummaryTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly Mission _mission = new() { Id = Guid.NewGuid(), Name = "Demo", StartsAt = DateTimeOffset.UtcNow };

    public McCrewSummaryTests()
    {
        _connection.Open();
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _db.Missions.Add(_mission);
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Astronaut AddAstronaut(string name)
    {
        var a = new Astronaut { Id = Guid.NewGuid(), UserName = name, DisplayName = name, MissionId = _mission.Id, UpdatedAt = DateTimeOffset.UtcNow };
        _db.Astronauts.Add(a);
        return a;
    }

    private CheckUp AddCheckUp(Astronaut a, DateTimeOffset at, HealthStatus status, Guid? supersedes = null)
    {
        var c = new CheckUp { Id = Guid.NewGuid(), AstronautId = a.Id, RecordedAt = at, ReceivedAt = at.AddMinutes(1), OverallStatus = status, RangesVersion = "v1", SupersedesId = supersedes };
        _db.CheckUps.Add(c);
        return c;
    }

    [Fact]
    public async Task Astronaut_without_check_ups_has_no_status()
    {
        AddAstronaut("new");
        await _db.SaveChangesAsync();

        var s = Assert.Single(await McCrewEndpoints.BuildSummariesAsync(_db, default));
        Assert.Null(s.OverallStatus);
        Assert.Null(s.LastCheckUpAt);
        Assert.Equal(0, s.OpenAlerts);
        Assert.Equal(0, s.UnreadMessages);
    }

    [Fact]
    public async Task Summary_uses_latest_check_up_and_ignores_superseded_ones()
    {
        var a = AddAstronaut("ana");
        var t = DateTimeOffset.UtcNow.AddHours(-5);
        AddCheckUp(a, t, HealthStatus.Normal);
        var wrong = AddCheckUp(a, t.AddHours(1), HealthStatus.Critical);
        var fix = AddCheckUp(a, t.AddHours(2), HealthStatus.Watch, supersedes: wrong.Id);
        await _db.SaveChangesAsync();

        var s = Assert.Single(await McCrewEndpoints.BuildSummariesAsync(_db, default));
        Assert.Equal(HealthStatus.Watch, s.OverallStatus);
        Assert.Equal(fix.RecordedAt, s.LastCheckUpAt);
        Assert.Equal(fix.ReceivedAt, s.LastSyncAt);
    }

    [Fact]
    public async Task Counts_only_open_alerts_and_unread_messages_to_mission_control()
    {
        var a = AddAstronaut("ana");
        var b = AddAstronaut("ben");
        var c1 = AddCheckUp(a, DateTimeOffset.UtcNow, HealthStatus.Critical);
        var c2 = AddCheckUp(a, DateTimeOffset.UtcNow.AddMinutes(1), HealthStatus.Critical);
        _db.Alerts.Add(new Alert { Id = Guid.NewGuid(), AstronautId = a.Id, CheckUpId = c1.Id, CreatedAt = DateTimeOffset.UtcNow });
        _db.Alerts.Add(new Alert { Id = Guid.NewGuid(), AstronautId = a.Id, CheckUpId = c2.Id, CreatedAt = DateTimeOffset.UtcNow, AcknowledgedAt = DateTimeOffset.UtcNow });
        Message M(Astronaut x, MessageDirection d, DateTimeOffset? read) => new() { Id = Guid.NewGuid(), AstronautId = x.Id, Direction = d, Kind = MessageKind.Message, Subject = "s", Body = "b", CreatedAt = DateTimeOffset.UtcNow, ReadAt = read };
        _db.Messages.AddRange(M(a, MessageDirection.ToMissionControl, null), M(a, MessageDirection.ToMissionControl, null),
            M(a, MessageDirection.ToMissionControl, DateTimeOffset.UtcNow), M(a, MessageDirection.ToAstronaut, null));
        await _db.SaveChangesAsync();

        var list = await McCrewEndpoints.BuildSummariesAsync(_db, default);
        var ana = list.Single(x => x.AstronautId == a.Id);
        Assert.Equal(1, ana.OpenAlerts);
        Assert.Equal(2, ana.UnreadMessages);
        var ben = list.Single(x => x.AstronautId == b.Id);
        Assert.Equal(0, ben.OpenAlerts);
        Assert.Equal(0, ben.UnreadMessages);
    }
}
