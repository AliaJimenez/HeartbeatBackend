using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Deployment;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Data.Seeding;

/// <summary>
/// Demo data for the three stories in CONTRACTS.md §7: roles, portal users, one mission, three astronauts and
/// 30 daily check-ups each. Every reading is evaluated with the shared <see cref="HealthEvaluator"/> (statuses and
/// band IDs are never typed by hand) and alerts follow the same rule as sync: one per critical check-up.
/// </summary>
/// <remarks>
/// Runs only when <c>Seed:Enabled</c> is true. Passwords are read from <c>Seed:Passwords:&lt;userName&gt;</c>, which the
/// repository defines only in appsettings.Development.json; a missing password stops the seed instead of using a default.
/// Idempotent: existing roles, users, the mission and astronauts are left alone. The 30 days end "today" (the seed
/// time), so charts always look current.
/// </summary>
public sealed class DemoDataSeeder(IConfiguration configuration, ILogger<DemoDataSeeder> logger) : IDataSeeder
{
    public const int Days = 30;
    public const string MissionName = "Artemis Demo 1";

    public static readonly IReadOnlyList<string> AccountNames = ["admin", "j.okafor", "m.chen", "a.rivera", "k.sato", "l.haddad"];

    private static readonly string[] OperatorPermissions =
    [
        Permissions.CrewView, Permissions.VitalsView, Permissions.AlertsView, Permissions.MessagesView, Permissions.MessagesSend,
    ];

    private static readonly string[] SurgeonPermissions =
    [
        Permissions.CrewView, Permissions.VitalsView, Permissions.VitalsHistory, Permissions.AlertsView,
        Permissions.AlertsAcknowledge, Permissions.MessagesView, Permissions.MessagesSend,
    ];

    private sealed record Fever(decimal Temp, decimal Heart, string? Note);

    private sealed record DayData(IReadOnlyList<ReadingInput> Readings, string? Note = null);

    private sealed record AstronautPlan(
        string UserName, string DisplayName, string CallSign, DateOnly DateOfBirth, TimeOnly CheckUpTime, Func<int, DayData> Day);

    private static readonly AstronautPlan[] Plans =
    [
        new("a.rivera", "Ana Rivera", "Comet", new DateOnly(1988, 3, 12), new TimeOnly(9, 30), Ana),
        new("k.sato", "Kenji Sato", "Atlas", new DateOnly(1985, 7, 21), new TimeOnly(10, 15), Kenji),
        new("l.haddad", "Leila Haddad", "Nova", new DateOnly(1990, 11, 2), new TimeOnly(8, 45), Leila),
    ];

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;

        // One transaction: Identity and the app tables are either all seeded or not at all.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var roles = await EnsureRolesAsync(db, cancellationToken);
        var portal = await EnsurePortalUsersAsync(db, users, roles, cancellationToken);
        var mission = await EnsureMissionAsync(db, cancellationToken);

        foreach (var plan in Plans)
        {
            await EnsureAstronautAsync(db, users, mission, plan, portal["j.okafor"], now, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Demo data seeded.");
    }

    // ----- Roles and portal users -----

    private static async Task<Dictionary<string, Role>> EnsureRolesAsync(AppDbContext db, CancellationToken ct)
    {
        var wanted = new Dictionary<string, IReadOnlyCollection<string>>
        {
            [RoleNames.Administrator] = Permissions.All.ToArray(),
            [RoleNames.FlightSurgeon] = SurgeonPermissions,
            [RoleNames.MissionControlOperator] = OperatorPermissions,
        };

        var result = new Dictionary<string, Role>();
        foreach (var (name, permissions) in wanted)
        {
            var role = await db.Roles.Include(r => r.Permissions).FirstOrDefaultAsync(r => r.Name == name, ct);
            if (role is null)
            {
                role = new Role { Id = Guid.CreateVersion7(), Name = name, IsSystem = true };
                foreach (var p in permissions)
                {
                    role.Permissions.Add(new RolePermission { RoleId = role.Id, Permission = p });
                }

                db.Roles.Add(role);
            }

            result[name] = role;
        }

        return result;
    }

    private async Task<Dictionary<string, PortalUser>> EnsurePortalUsersAsync(
        AppDbContext db, UserManager<ApplicationUser> users, Dictionary<string, Role> roles, CancellationToken ct)
    {
        var wanted = new (string UserName, string DisplayName, string Role)[]
        {
            ("admin", "Administrator", RoleNames.Administrator),
            ("j.okafor", "Dr. J. Okafor", RoleNames.FlightSurgeon),
            ("m.chen", "M. Chen", RoleNames.MissionControlOperator),
        };

        var result = new Dictionary<string, PortalUser>();
        foreach (var (userName, displayName, roleName) in wanted)
        {
            var existing = await db.PortalUsers.FirstOrDefaultAsync(u => u.UserName == userName, ct);
            if (existing is null)
            {
                var id = Guid.CreateVersion7();
                var email = $"{userName}@heartbeat.demo";
                await CreateIdentityAsync(users, new ApplicationUser
                {
                    Id = id, UserName = userName, Email = email, DisplayName = displayName, AccountType = AccountType.Portal,
                }, userName);

                existing = new PortalUser
                {
                    Id = id, UserName = userName, Email = email, DisplayName = displayName, RoleId = roles[roleName].Id, IsActive = true,
                };
                db.PortalUsers.Add(existing);
            }

            result[userName] = existing;
        }

        return result;
    }

    private async Task CreateIdentityAsync(UserManager<ApplicationUser> users, ApplicationUser user, string userName)
    {
        var password = configuration[$"Seed:Passwords:{userName}"];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Seed:Passwords:{userName} is not configured. Demo passwords live only in appsettings.Development.json " +
                "(or the Seed__Passwords__... environment variables); the seed never uses a default password.");
        }

        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"Could not create demo user '{userName}': {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }

    public const string MarsMissionName = "Artemis Mars Transit";

    private static async Task<Mission> EnsureMissionAsync(AppDbContext db, CancellationToken ct)
    {
        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Name == MissionName, ct);
        if (mission is null)
        {
            mission = new Mission
            {
                Id = Guid.CreateVersion7(),
                Name = MissionName,
                Environment = "station",
                EnvironmentType = "station",
                GravityG = 0m,
                DistanceKm = 408m,
                CrewSize = 4,
                HabitableVolumeM3 = 388m,
                StartsAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            };
            db.Missions.Add(mission);
        }
        else
        {
            mission.EnvironmentType = string.IsNullOrWhiteSpace(mission.EnvironmentType) ? mission.Environment : mission.EnvironmentType;
        }

        // Second profile: transit to Mars (HB-BE-04).
        var mars = await db.Missions.FirstOrDefaultAsync(m => m.Name == MarsMissionName, ct);
        if (mars is null)
        {
            mars = new Mission
            {
                Id = Guid.CreateVersion7(),
                Name = MarsMissionName,
                Environment = "spacecraft",
                EnvironmentType = "spacecraft",
                GravityG = 0m,
                DistanceKm = 225000000m,
                CrewSize = 4,
                HabitableVolumeM3 = 100m,
                StartsAt = new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero),
            };
            db.Missions.Add(mars);
        }

        return mission;
    }

    // ----- Astronauts, check-ups, alerts and messages -----

    private async Task EnsureAstronautAsync(
        AppDbContext db, UserManager<ApplicationUser> users, Mission mission, AstronautPlan plan, PortalUser surgeon,
        DateTimeOffset now, CancellationToken ct)
    {
        if (await db.Astronauts.AnyAsync(a => a.UserName == plan.UserName, ct))
        {
            return;
        }

        var id = Guid.CreateVersion7();
        await CreateIdentityAsync(users, new ApplicationUser
        {
            Id = id, UserName = plan.UserName, DisplayName = plan.DisplayName, AccountType = AccountType.Astronaut,
        }, plan.UserName);

        var astronaut = new Astronaut
        {
            Id = id,
            UserName = plan.UserName,
            DisplayName = plan.DisplayName,
            CallSign = plan.CallSign,
            MissionId = mission.Id,
            DateOfBirth = plan.DateOfBirth,
            UpdatedAt = now,
        };
        db.Astronauts.Add(astronaut);

        var evaluator = new HealthEvaluator();
        var lastRecorded = LastCheckUpTime(now, plan.CheckUpTime);
        var checkUpsByDay = new Dictionary<int, CheckUp>();

        // Per-environment hazard values for the simulated stories (all flagged simulated = true).
        var environment = mission.EnvironmentType ?? mission.Environment;
        var radiationRate = RadiationDoses.RateForEnvironment(environment);

        for (var day = 1; day <= Days; day++)
        {
            var data = plan.Day(day);
            var errors = data.Readings.SelectMany(evaluator.Validate).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Seed data for {plan.UserName} day {day} is invalid: {errors[0]}");
            }

            var evaluation = evaluator.Evaluate(data.Readings);
            var recordedAt = lastRecorded.AddDays(day - Days);
            var checkUp = new CheckUp
            {
                Id = Guid.CreateVersion7(),
                AstronautId = id,
                RecordedAt = recordedAt,
                ReceivedAt = Earlier(recordedAt.AddMinutes(5), now),
                OverallStatus = evaluation.OverallStatus,
                RangesVersion = evaluation.RangesVersion,
                Note = data.Note,
            };

            for (var i = 0; i < data.Readings.Count; i++)
            {
                var reading = data.Readings[i];
                var evaluated = evaluation.Readings[i];
                checkUp.Readings.Add(new Reading
                {
                    Id = Guid.CreateVersion7(),
                    Indicator = reading.Indicator,
                    Value = reading.Value,
                    SecondaryValue = reading.SecondaryValue,
                    Status = evaluated.Status,
                    BandIds = string.Join(",", evaluated.BandIds),
                });
            }

            db.CheckUps.Add(checkUp);
            checkUpsByDay[day] = checkUp;

            // Same rule as POST /sync: every critical check-up raises exactly one alert.
            if (evaluation.OverallStatus == HealthStatus.Critical)
            {
                db.Alerts.Add(new Alert
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = id,
                    CheckUpId = checkUp.Id,
                    Indicators = string.Join(",", evaluation.Readings.Where(r => r.Status == HealthStatus.Critical).Select(r => r.Indicator.ToString()).Distinct()),
                    CreatedAt = checkUp.ReceivedAt!.Value,
                });
            }
        }

        // Simulated environment readings for the habitat (CO2 spike, temperature, humidity, light, noise, O2, pressure).
        SeedEnvironmentReadings(db, mission, id, lastRecorded, now);

        // Simulated radiation doses: a steady GCR background plus a single SPE on day 20 for Leila.
        SeedRadiationDoses(db, id, lastRecorded, radiationRate, plan.UserName == "l.haddad", now);

        // Simulated IOP / PVT-B readings are stored as environment readings keyed by indicator id.
        SeedSimulatedIndicators(db, id, lastRecorded, plan, now);

        // The SPE and the CO2 spike above would have raised alerts through POST /sync; they were handled 10 days ago.
        SeedHandledEventAlerts(db, id, surgeon, now);

        AddStoryMessages(db, plan, id, surgeon, checkUpsByDay, now);
    }

    // ----- Simulated data for the new hazards (HB-DA-02). All rows are marked simulated = true. -----

    private static void SeedEnvironmentReadings(AppDbContext db, Mission mission, Guid astronautId, DateTimeOffset lastRecorded, DateTimeOffset now)
    {
        // Deterministic pseudo-random based on a fixed seed so the demo is reproducible.
        var rng = new Random(42);
        var missionId = mission.Id;

        for (var day = 1; day <= Days; day++)
        {
            var recordedAt = lastRecorded.AddDays(day - Days);
            var receivedAt = Earlier(recordedAt.AddMinutes(3), now);

            // CO2 spike on day 20 (simulated event).
            var co2 = day == 20 ? 5.6m : 2.4m + (decimal)(rng.NextDouble() * 0.6 - 0.3);
            AddEnvironmentReading(db, missionId, astronautId, "co2", Math.Round(co2, 2), "mmHg", recordedAt, receivedAt, true);

            AddEnvironmentReading(db, missionId, astronautId, "o2", 162m + (decimal)(rng.NextDouble() * 6 - 3), "mmHg", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId, astronautId, "pressure", 101.3m + (decimal)(rng.NextDouble() * 2 - 1), "kPa", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId, astronautId, "temperature", 21.5m + (decimal)(rng.NextDouble() * 1.5 - 0.75), "°C", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId, astronautId, "humidity", 45m + (decimal)(rng.NextDouble() * 10 - 5), "%", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId, astronautId, "light", 320m + (decimal)(rng.NextDouble() * 80 - 40), "lux", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId, astronautId, "noise", 52m + (decimal)(rng.NextDouble() * 8 - 4), "dB", recordedAt, receivedAt, true);
        }
    }

    private static void SeedRadiationDoses(AppDbContext db, Guid astronautId, DateTimeOffset lastRecorded, decimal rate, bool hasSpe, DateTimeOffset now)
    {
        // Steady GCR background: one dose per week at the environment rate.
        for (var week = 1; week <= 4; week++)
        {
            var recordedAt = lastRecorded.AddDays(week * 7 - Days);
            db.RadiationDoses.Add(new RadiationDose
            {
                Id = Guid.CreateVersion7(),
                AstronautId = astronautId,
                RecordedAt = recordedAt,
                ReceivedAt = Earlier(recordedAt.AddMinutes(5), now),
                DoseMsv = Math.Round(rate * 7m, 2),
                Type = RadiationType.Gcr,
                Note = "GCR background",
            });
        }

        // A single SPE on day 20 (only for Leila's story).
        if (hasSpe)
        {
            var recordedAt = lastRecorded.AddDays(20 - Days);
            db.RadiationDoses.Add(new RadiationDose
            {
                Id = Guid.CreateVersion7(),
                AstronautId = astronautId,
                RecordedAt = recordedAt,
                ReceivedAt = Earlier(recordedAt.AddMinutes(5), now),
                DoseMsv = 180m,
                Type = RadiationType.Spe,
                Note = "Solar particle event",
            });
        }
    }

    // Same rules as POST /sync: every solar particle event raises a radiation alert and every critical habitat reading an
    // environment alert. The seeded events are old, so the flight surgeon already acknowledged them: they tell the
    // story in the alert history without adding open alerts.
    private static void SeedHandledEventAlerts(AppDbContext db, Guid astronautId, PortalUser surgeon, DateTimeOffset now)
    {
        foreach (var dose in db.RadiationDoses.Local.Where(d => d.AstronautId == astronautId && d.Type == RadiationType.Spe).ToList())
        {
            AddHandledAlert(
                db, astronautId, surgeon, now, dose.ReceivedAt ?? dose.RecordedAt, AlertKind.Radiation,
                RadiationDoses.AlertSpe, RadiationDoses.SpeAlertMessage(dose.DoseMsv), "Crew sheltered, dose monitored",
                radiationDoseId: dose.Id);
        }

        foreach (var reading in db.EnvironmentReadings.Local.Where(r => r.AstronautId == astronautId).ToList())
        {
            var entry = Heartbeat.Api.Crew.EnvironmentReadings.EnvironmentIndicator(reading.IndicatorId);
            if (entry is null)
            {
                continue;
            }

            var band = IndicatorsCatalog.Evaluate(entry, reading.Value);
            if (band.Status != HealthStatus.Critical)
            {
                continue;
            }

            AddHandledAlert(
                db, astronautId, surgeon, now, reading.ReceivedAt ?? reading.RecordedAt, AlertKind.Environment,
                Heartbeat.Api.Crew.EnvironmentReadings.AlertMarker(entry.Id),
                Heartbeat.Api.Crew.EnvironmentReadings.AlertMessage(entry, band, reading.Value), "Scrubber swapped, back to normal");
        }
    }

    private static void AddHandledAlert(
        AppDbContext db, Guid astronautId, PortalUser surgeon, DateTimeOffset now, DateTimeOffset createdAt,
        AlertKind kind, string indicators, string message, string note, Guid? radiationDoseId = null)
    {
        db.Alerts.Add(new Alert
        {
            Id = Guid.CreateVersion7(),
            AstronautId = astronautId,
            Kind = kind,
            RadiationDoseId = radiationDoseId,
            Indicators = indicators,
            Message = message,
            CreatedAt = createdAt,
            AcknowledgedAt = Earlier(createdAt.AddHours(1), now),
            AcknowledgedByUserId = surgeon.Id,
            AcknowledgeNote = note,
        });
    }

    private static void SeedSimulatedIndicators(AppDbContext db, Guid astronautId, DateTimeOffset lastRecorded, AstronautPlan plan, DateTimeOffset now)
    {
        // IOP and PVT-B as environment readings keyed by their indicator id. A sleep-deprivation streak
        // for Kenji over the last 14 days raises PVT-B (slower reaction time).
        var rng = new Random(7);
        bool sleepDeprived = plan.UserName == "k.sato";

        for (var day = 1; day <= Days; day++)
        {
            var recordedAt = lastRecorded.AddDays(day - Days);
            var receivedAt = Earlier(recordedAt.AddMinutes(4), now);

            var progress = sleepDeprived && day > Days - 14 ? (day - (Days - 14)) / 14.0 : 0.0;
            var iop = 14m + (decimal)(rng.NextDouble() * 4 - 2);
            var pvt = 320m + (decimal)(rng.NextDouble() * 20 - 10) + (decimal)(40 * progress);

            AddEnvironmentReading(db, missionId: null, astronautId, "iop", Math.Round(iop, 1), "mmHg", recordedAt, receivedAt, true);
            AddEnvironmentReading(db, missionId: null, astronautId, "pvtb", Math.Round(pvt, 0), "ms", recordedAt, receivedAt, true);
        }
    }

    private static void AddEnvironmentReading(
        AppDbContext db, Guid? missionId, Guid? astronautId, string indicatorId, decimal value, string unit,
        DateTimeOffset recordedAt, DateTimeOffset receivedAt, bool simulated)
    {
        db.EnvironmentReadings.Add(new Heartbeat.Api.Data.Entities.EnvironmentReading
        {
            Id = Guid.CreateVersion7(),
            MissionId = missionId,
            AstronautId = astronautId,
            IndicatorId = indicatorId,
            Value = Math.Round(value, 2),   // the random walk gives 14 decimals; the instruments do not
            Unit = unit,
            RecordedAt = recordedAt,
            ReceivedAt = receivedAt,
            Simulated = simulated,
        });
    }

    private static void AddStoryMessages(
        AppDbContext db, AstronautPlan plan, Guid astronautId, PortalUser surgeon, Dictionary<int, CheckUp> checkUps, DateTimeOffset now)
    {
        switch (plan.UserName)
        {
            case "a.rivera":
            {
                // Open alert + an unread report for the day-30 blood-pressure spike.
                var spike = checkUps[Days];
                db.Messages.Add(new Message
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Direction = MessageDirection.ToMissionControl,
                    Kind = MessageKind.Report,
                    CheckUpId = spike.Id,
                    Subject = "Dizzy after exercise",
                    Body = "BP high after treadmill; resting now.",
                    CreatedAt = spike.RecordedAt.AddMinutes(2),
                    ReceivedAt = spike.ReceivedAt,
                });
                break;
            }

            case "l.haddad":
            {
                // Fever peaked on day 20: report (read), acknowledged alert and the flight surgeon's reply.
                var peak = checkUps[20];
                var acknowledgedAt = Earlier(peak.RecordedAt.AddHours(1), now);
                var alert = db.Alerts.Local.Single(a => a.CheckUpId == peak.Id);
                alert.AcknowledgedAt = acknowledgedAt;
                alert.AcknowledgedByUserId = surgeon.Id;
                alert.AcknowledgeNote = "Rest 24h, recheck";

                db.Messages.Add(new Message
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Direction = MessageDirection.ToMissionControl,
                    Kind = MessageKind.Report,
                    CheckUpId = peak.Id,
                    Subject = "High fever",
                    Body = "39.2 °C this morning with chills.",
                    CreatedAt = peak.RecordedAt.AddMinutes(2),
                    ReceivedAt = peak.ReceivedAt,
                    ReadAt = acknowledgedAt,
                });
                db.Messages.Add(new Message
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Direction = MessageDirection.ToAstronaut,
                    Kind = MessageKind.Message,
                    Subject = "Re: High fever",
                    Body = "Rest for 24 hours, drink fluids and recheck your temperature tomorrow morning.",
                    CreatedAt = acknowledgedAt,
                    ReceivedAt = acknowledgedAt,
                    SenderUserId = surgeon.Id,
                });
                break;
            }
        }
    }

    // ----- Time -----

    /// <summary>The most recent check-up time that is not in the future (today at the astronaut's usual hour, else yesterday).</summary>
    private static DateTimeOffset LastCheckUpTime(DateTimeOffset now, TimeOnly time)
    {
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(time), TimeSpan.Zero);
        return today > now.AddMinutes(-10) ? today.AddDays(-1) : today;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // ----- The three stories (day 1 = oldest, day 30 = most recent). Pure functions: no randomness. -----

    // Smooth, deterministic variation in [-1, 1] so charts look alive but stay inside the "normal" bands.
    private static double Wave(int day, int channel) => Math.Sin(day * 1.7 + channel * 2.3);

    private static decimal Jitter(decimal baseValue, double amplitude, int day, int channel, int decimals = 0)
        => decimal.Round(baseValue + (decimal)(amplitude * Wave(day, channel)), decimals, MidpointRounding.AwayFromZero);

    private static DayData Reads(
        decimal systolic, decimal diastolic, decimal heartRate, decimal sugar, decimal temperature, decimal sleep, decimal mood, string? note = null)
        => new(
        [
            new ReadingInput(IndicatorType.BloodPressure, systolic, diastolic),
            new ReadingInput(IndicatorType.HeartRate, heartRate),
            new ReadingInput(IndicatorType.BloodSugar, sugar),
            new ReadingInput(IndicatorType.BodyTemperature, temperature),
            new ReadingInput(IndicatorType.Sleep, sleep),
            new ReadingInput(IndicatorType.Mood, mood),
        ], note);

    // Ana: healthy for 29 days, then a critical blood-pressure spike on day 30.
    private static DayData Ana(int day)
    {
        if (day == Days)
        {
            return Reads(185, 95, 88, Jitter(92, 6, day, 4), 36.6m, 7.0m, 4, "Felt dizzy after exercise");
        }

        return Reads(
            Jitter(118, 4, day, 1), Jitter(76, 3, day, 2), Jitter(64, 4, day, 3), Jitter(92, 6, day, 4),
            Jitter(36.6m, 0.2, day, 5, 1), Jitter(7.2m, 0.5, day, 6, 1), Wave(day, 7) > 0.6 ? 5 : 4);
    }

    // Kenji: sleep slides from 7 h to 4.5 h over the last two weeks and mood falls to 2 (watch, never critical).
    private static DayData Kenji(int day)
    {
        const int slideStart = Days - 14;
        var progress = day <= slideStart ? 0.0 : (day - slideStart) / 14.0;
        var sleep = progress == 0
            ? Jitter(7.0m, 0.2, day, 6, 1)
            : decimal.Round(7.0m - 2.5m * (decimal)progress, 1);
        var mood = decimal.Round(4m - 2m * (decimal)progress, 0, MidpointRounding.AwayFromZero);

        return Reads(
            Jitter(112, 4, day, 1), Jitter(72, 3, day, 2), Jitter(58, 4, day, 3), Jitter(88, 6, day, 4),
            Jitter(36.5m, 0.2, day, 5, 1), sleep, mood,
            day == Days ? "Waking up several times a night" : null);
    }

    // Leila: fever on days 18-20 (38.4 -> 39.2, critical on day 20), then recovery.
    private static DayData Leila(int day)
    {
        Fever? fever = day switch
        {
            18 => new Fever(38.4m, 96m, "Feeling feverish"),
            19 => new Fever(38.8m, 104m, null),
            20 => new Fever(39.2m, 112m, "Fever persists, very tired"),
            21 => new Fever(37.8m, 90m, null),
            22 => new Fever(37.2m, 76m, "Fever gone"),
            _ => null,
        };

        var feverish = day is >= 18 and <= 20;
        return Reads(
            Jitter(108, 4, day, 1), Jitter(68, 3, day, 2),
            fever?.Heart ?? Jitter(68, 4, day, 3),
            Jitter(85, 6, day, 4),
            fever?.Temp ?? Jitter(36.7m, 0.2, day, 5, 1),
            feverish ? 6.5m : Jitter(7.5m, 0.4, day, 6, 1),
            feverish ? 3 : 4,
            fever?.Note);
    }
}
