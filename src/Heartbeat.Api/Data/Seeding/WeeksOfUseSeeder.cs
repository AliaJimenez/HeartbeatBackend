using Heartbeat.Api.Data.Entities;
using Heartbeat.Api.Deployment;
using Heartbeat.Shared;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Heartbeat.Api.Data.Seeding;

/// <summary>
/// Makes the demo look like a platform with weeks of use. It runs after <see cref="DemoDataSeeder"/> and adds: 30 more
/// days of history for the three seeded astronauts (60 days in total), a fourth astronaut (the mission crew is four) with
/// 60 days, extra portal users (one disabled), weekly mission-control messages, report/reply threads, handled alerts,
/// habitat readings and radiation doses for the added days. The mission profiles are filled in when they are still zero.
/// </summary>
/// <remarks>
/// Same rules as the base seed: runs only when <c>Seed:Enabled</c> is true, every status and band comes from the shared
/// <see cref="HealthEvaluator"/>, every critical check-up raises exactly one alert (already acknowledged, because it is old),
/// and everything not entered by a crew member is simulated. Idempotent: it stops when <see cref="MarkerUser"/> exists.
/// Passwords are read from <c>Seed:Passwords:&lt;userName&gt;</c> (appsettings.Development.json only).
/// </remarks>
public sealed class WeeksOfUseSeeder(IConfiguration configuration, ILogger<WeeksOfUseSeeder> logger) : IDataSeeder
{
    public const string MarkerUser = "s.novak";

    private const int BackfillDays = 30;
    private const int NewAstronautDays = 60;
    private const int HabitatSpikeDay = 12; // same calendar date for the backfilled crew and the new astronaut

    private sealed record DayData(IReadOnlyList<ReadingInput> Readings, string? Note = null);

    private sealed record Day(int Number, DateTimeOffset At, DayData Data);

    private static readonly string[] WeeklyPlans =
    [
        "Week plan: exercise block Tuesday and Thursday. Keep your check-up at the usual time.",
        "Reminder: resupply docking on Wednesday, expect noise in module 2 around 14:00 UTC.",
        "Mission control maintenance window on Saturday 02:00-04:00 UTC: syncs will be delayed, nothing is lost.",
        "Please confirm your sleep schedule for the EVA preparation week.",
        "Medical review on Friday. No action needed if your check-ups are up to date.",
        "The cargo transfer moves to Thursday. Keep your check-up time.",
        "Hydration reminder: aim for 2.5 L of water a day, especially after exercise.",
        "Crew-wide fire and depressurization drill on Monday, 20 minutes.",
    ];

    public async Task SeedAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var users = services.GetRequiredService<UserManager<ApplicationUser>>();
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        await EnsureMissionProfilesAsync(db, cancellationToken);

        if (await db.PortalUsers.AnyAsync(u => u.UserName == MarkerUser, cancellationToken))
        {
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return;
        }

        var mission = await db.Missions.FirstOrDefaultAsync(m => m.Name == DemoDataSeeder.MissionName, cancellationToken);
        var okafor = await db.PortalUsers.FirstOrDefaultAsync(u => u.UserName == "j.okafor", cancellationToken);
        var chen = await db.PortalUsers.FirstOrDefaultAsync(u => u.UserName == "m.chen", cancellationToken);
        if (mission is null || okafor is null || chen is null)
        {
            logger.LogWarning("Weeks-of-use seed skipped: the base demo data is not there yet.");
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return;
        }

        var roles = await db.Roles.ToDictionaryAsync(r => r.Name, cancellationToken);
        var novak = await AddPortalUserAsync(db, users, roles, MarkerUser, "Dr. S. Novak", RoleNames.FlightSurgeon, true);
        var diaz = await AddPortalUserAsync(db, users, roles, "r.diaz", "R. Díaz", RoleNames.MissionControlOperator, true);
        await AddPortalUserAsync(db, users, roles, "t.park", "T. Park", RoleNames.MissionControlOperator, false);

        var surgeons = new[] { okafor, novak };
        var operators = new[] { chen, diaz };
        var evaluator = new HealthEvaluator();
        var rate = RadiationDoses.RateForEnvironment(mission.EnvironmentType ?? mission.Environment);

        // ----- The three base astronauts: 30 more days, ending the day before their first check-up. -----
        foreach (var userName in new[] { "a.rivera", "k.sato", "l.haddad" })
        {
            var astronaut = await db.Astronauts.FirstOrDefaultAsync(a => a.UserName == userName, cancellationToken);
            if (astronaut is null)
            {
                continue;
            }

            // SQLite cannot order or aggregate DateTimeOffset in SQL, so the sort happens here.
            var existing = (await db.CheckUps.Where(c => c.AstronautId == astronaut.Id).ToListAsync(cancellationToken))
                .OrderBy(c => c.RecordedAt).ToList();
            if (existing.Count == 0)
            {
                continue;
            }

            var earliest = existing[0].RecordedAt;
            var days = Enumerable.Range(1, BackfillDays)
                .Select(d => new Day(d, earliest.AddDays(d - 1 - BackfillDays), Backfill(userName, d)))
                .ToList();

            var checkUps = AddCheckUps(db, astronaut.Id, days, evaluator, surgeons, now);
            AddHabitat(db, mission, astronaut.Id, days, 100 + userName.Length + userName[0], surgeons, now);
            AddWeeklyGcr(db, astronaut.Id, Enumerable.Range(1, 4).Select(w => earliest.AddDays(-7 * w)), rate, now);

            astronaut.Notes ??= userName switch
            {
                "a.rivera" => "Allergic to penicillin. Prefers morning check-ups.",
                "k.sato" => "No known allergies. Mild seasonal rhinitis.",
                _ => "Lactose intolerant.",
            };

            AddThreads(db, userName, astronaut.Id, checkUps, existing, surgeons, operators, earliest, now);
        }

        // ----- The fourth astronaut: 60 days of his own, ending at his usual check-up time. -----
        var okoye = await AddAstronautAsync(db, users, mission, "d.okoye", "Daniel Okoye", "Vega", new DateOnly(1983, 2, 27), now);
        var last = LastCheckUpTime(now, new TimeOnly(7, 50));
        var okoyeDays = Enumerable.Range(1, NewAstronautDays)
            .Select(d => new Day(d, last.AddDays(d - NewAstronautDays), Okoye(d)))
            .ToList();
        var okoyeCheckUps = AddCheckUps(db, okoye.Id, okoyeDays, evaluator, surgeons, now);
        AddHabitat(db, mission, okoye.Id, okoyeDays, 7, surgeons, now);
        AddWeeklyGcr(db, okoye.Id, Enumerable.Range(1, 8).Select(w => last.AddDays(-7 * w)), rate, now);
        AddOkoyeThread(db, okoye.Id, okoyeCheckUps, surgeons, now);

        // ----- Weekly mission-control plans for the whole crew (the latest one is still unread). -----
        foreach (var astronaut in await db.Astronauts.Where(a => a.MissionId == mission.Id).ToListAsync(cancellationToken))
        {
            AddWeeklyPlans(db, astronaut.Id, mission, operators, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        logger.LogInformation("Weeks-of-use demo data seeded.");
    }

    // ----- Missions, users, astronauts -----

    private static async Task EnsureMissionProfilesAsync(AppDbContext db, CancellationToken ct)
    {
        foreach (var mission in await db.Missions.ToListAsync(ct))
        {
            if (mission.CrewSize > 0)
            {
                continue;
            }

            var mars = mission.Name == DemoDataSeeder.MarsMissionName;
            mission.CrewSize = 4;
            mission.DistanceKm = mars ? 225000000m : 408m;
            mission.HabitableVolumeM3 = mars ? 100m : 388m;
            mission.EnvironmentType = string.IsNullOrWhiteSpace(mission.EnvironmentType) ? mission.Environment : mission.EnvironmentType;
        }
    }

    private async Task<PortalUser> AddPortalUserAsync(
        AppDbContext db, UserManager<ApplicationUser> users, Dictionary<string, Role> roles,
        string userName, string displayName, string role, bool active)
    {
        var id = Guid.CreateVersion7();
        var email = $"{userName}@heartbeat.demo";
        await CreateIdentityAsync(users, new ApplicationUser
        {
            Id = id, UserName = userName, Email = email, DisplayName = displayName, AccountType = AccountType.Portal,
        }, userName);

        var portalUser = new PortalUser
        {
            Id = id, UserName = userName, Email = email, DisplayName = displayName, RoleId = roles[role].Id, IsActive = active,
        };
        db.PortalUsers.Add(portalUser);
        return portalUser;
    }

    private async Task<Astronaut> AddAstronautAsync(
        AppDbContext db, UserManager<ApplicationUser> users, Mission mission, string userName, string displayName,
        string callSign, DateOnly dateOfBirth, DateTimeOffset now)
    {
        var id = Guid.CreateVersion7();
        await CreateIdentityAsync(users, new ApplicationUser
        {
            Id = id, UserName = userName, DisplayName = displayName, AccountType = AccountType.Astronaut,
        }, userName);

        var astronaut = new Astronaut
        {
            Id = id,
            UserName = userName,
            DisplayName = displayName,
            CallSign = callSign,
            MissionId = mission.Id,
            DateOfBirth = dateOfBirth,
            Notes = "Left-handed. No known allergies.",
            UpdatedAt = now,
        };
        db.Astronauts.Add(astronaut);
        return astronaut;
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

    // ----- Check-ups and their alerts -----

    private static Dictionary<int, CheckUp> AddCheckUps(
        AppDbContext db, Guid astronautId, IReadOnlyList<Day> days, HealthEvaluator evaluator,
        IReadOnlyList<PortalUser> surgeons, DateTimeOffset now)
    {
        var result = new Dictionary<int, CheckUp>();
        foreach (var day in days)
        {
            var errors = day.Data.Readings.SelectMany(evaluator.Validate).ToList();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException($"Weeks-of-use seed data for day {day.Number} is invalid: {errors[0]}");
            }

            var evaluation = evaluator.Evaluate(day.Data.Readings);
            var checkUp = new CheckUp
            {
                Id = Guid.CreateVersion7(),
                AstronautId = astronautId,
                RecordedAt = day.At,
                ReceivedAt = Earlier(day.At.AddMinutes(5), now),
                OverallStatus = evaluation.OverallStatus,
                RangesVersion = evaluation.RangesVersion,
                Note = day.Data.Note,
            };

            for (var i = 0; i < day.Data.Readings.Count; i++)
            {
                var reading = day.Data.Readings[i];
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
            result[day.Number] = checkUp;

            // Same rule as POST /sync: one alert per critical check-up. These are old, so a surgeon already handled them.
            if (evaluation.OverallStatus == HealthStatus.Critical)
            {
                var surgeon = surgeons[day.Number % surgeons.Count];
                db.Alerts.Add(new Alert
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    CheckUpId = checkUp.Id,
                    Indicators = string.Join(",", evaluation.Readings.Where(r => r.Status == HealthStatus.Critical).Select(r => r.Indicator.ToString()).Distinct()),
                    CreatedAt = checkUp.ReceivedAt!.Value,
                    AcknowledgedAt = Earlier(checkUp.ReceivedAt.Value.AddHours(2), now),
                    AcknowledgedByUserId = surgeon.Id,
                    AcknowledgeNote = "Rest, hydrate and recheck in 2 h; follow-up call tomorrow",
                });
            }
        }

        return result;
    }

    // ----- Habitat readings (simulated), indicators IOP/PVT-B (simulated) and handled environment alerts -----

    private static void AddHabitat(
        AppDbContext db, Mission mission, Guid astronautId, IReadOnlyList<Day> days, int seed,
        IReadOnlyList<PortalUser> surgeons, DateTimeOffset now)
    {
        var rng = new Random(seed);
        foreach (var day in days)
        {
            var receivedAt = Earlier(day.At.AddMinutes(3), now);
            var co2 = day.Number == HabitatSpikeDay ? 5.6m : 2.4m + (decimal)(rng.NextDouble() * 0.6 - 0.3);
            var readings = new (string Id, decimal Value, string Unit)[]
            {
                ("co2", Math.Round(co2, 2), "mmHg"),
                ("o2", 162m + (decimal)(rng.NextDouble() * 6 - 3), "mmHg"),
                ("pressure", 101.3m + (decimal)(rng.NextDouble() * 2 - 1), "kPa"),
                ("temperature", 21.5m + (decimal)(rng.NextDouble() * 1.5 - 0.75), "°C"),
                ("humidity", 45m + (decimal)(rng.NextDouble() * 10 - 5), "%"),
                ("light", 320m + (decimal)(rng.NextDouble() * 80 - 40), "lux"),
                ("noise", 52m + (decimal)(rng.NextDouble() * 8 - 4), "dB"),
            };

            foreach (var (id, value, unit) in readings)
            {
                var reading = AddEnvironmentReading(db, mission.Id, astronautId, id, value, unit, day.At, receivedAt);
                var entry = Heartbeat.Api.Crew.EnvironmentReadings.EnvironmentIndicator(id);
                if (entry is null)
                {
                    continue;
                }

                var band = IndicatorsCatalog.Evaluate(entry, reading.Value);
                if (band.Status != HealthStatus.Critical)
                {
                    continue;
                }

                db.Alerts.Add(new Alert
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Kind = AlertKind.Environment,
                    Indicators = Heartbeat.Api.Crew.EnvironmentReadings.AlertMarker(entry.Id),
                    Message = Heartbeat.Api.Crew.EnvironmentReadings.AlertMessage(entry, band, reading.Value),
                    CreatedAt = receivedAt,
                    AcknowledgedAt = Earlier(receivedAt.AddHours(1), now),
                    AcknowledgedByUserId = surgeons[day.Number % surgeons.Count].Id,
                    AcknowledgeNote = "Scrubber filter replaced, CO2 back to normal",
                });
            }

            // IOP and PVT-B, keyed by indicator id like the base seed (simulated).
            var iop = 14m + (decimal)(rng.NextDouble() * 4 - 2);
            var pvt = 320m + (decimal)(rng.NextDouble() * 20 - 10);
            AddEnvironmentReading(db, null, astronautId, "iop", Math.Round(iop, 1), "mmHg", day.At, Earlier(day.At.AddMinutes(4), now));
            AddEnvironmentReading(db, null, astronautId, "pvtb", Math.Round(pvt, 0), "ms", day.At, Earlier(day.At.AddMinutes(4), now));
        }
    }

    private static Heartbeat.Api.Data.Entities.EnvironmentReading AddEnvironmentReading(
        AppDbContext db, Guid? missionId, Guid astronautId, string indicatorId, decimal value, string unit,
        DateTimeOffset recordedAt, DateTimeOffset receivedAt)
    {
        var reading = new Heartbeat.Api.Data.Entities.EnvironmentReading
        {
            Id = Guid.CreateVersion7(),
            MissionId = missionId,
            AstronautId = astronautId,
            IndicatorId = indicatorId,
            Value = Math.Round(value, 2),
            Unit = unit,
            RecordedAt = recordedAt,
            ReceivedAt = receivedAt,
            Simulated = true,
        };
        db.EnvironmentReadings.Add(reading);
        return reading;
    }

    private static void AddWeeklyGcr(AppDbContext db, Guid astronautId, IEnumerable<DateTimeOffset> weeks, decimal rate, DateTimeOffset now)
    {
        foreach (var recordedAt in weeks)
        {
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
    }

    // ----- Messages -----

    private static void AddWeeklyPlans(AppDbContext db, Guid astronautId, Mission mission, IReadOnlyList<PortalUser> operators, DateTimeOffset now)
    {
        var monday = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(8);
        for (var w = 0; w < WeeklyPlans.Length; w++)
        {
            var createdAt = Earlier(monday.AddDays(-7 * w), now.AddMinutes(-30));
            var weekNumber = (int)((createdAt - mission.StartsAt).TotalDays / 7) + 1;
            db.Messages.Add(NewMessage(
                astronautId, MessageDirection.ToAstronaut, MessageKind.Message, $"Week {weekNumber} plan",
                WeeklyPlans[(w + astronautId.GetHashCode() % 3 + WeeklyPlans.Length) % WeeklyPlans.Length], createdAt, now,
                sender: operators[w % operators.Count].Id,
                readAt: w == 0 ? null : createdAt.AddHours(1 + w % 4)));
        }
    }

    private static void AddThreads(
        AppDbContext db, string userName, Guid astronautId, Dictionary<int, CheckUp> back, IReadOnlyList<CheckUp> window,
        IReadOnlyList<PortalUser> surgeons, IReadOnlyList<PortalUser> operators, DateTimeOffset earliest, DateTimeOffset now)
    {
        switch (userName)
        {
            case "a.rivera":
            {
                var spike = back[11];
                var ack = spike.ReceivedAt!.Value.AddHours(2);
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Report, "Headache and high BP",
                    "Woke up with a headache. BP was 186/98 after the stressful simulation week. Resting now.",
                    spike.RecordedAt.AddMinutes(3), now, spike.Id, readAt: ack));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Re: Headache and high BP",
                    "Please rest, hydrate and recheck in two hours. Tell us if the headache persists; we will call you tomorrow.",
                    ack, now, sender: surgeons[11 % surgeons.Count].Id, readAt: ack.AddMinutes(25)));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Message, "Treadmill vibration",
                    "The treadmill vibrates at high speed. Could someone check the mounts before the next block?",
                    earliest.AddDays(-9), now, readAt: earliest.AddDays(-9).AddHours(2)));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Re: Treadmill vibration",
                    "Maintenance logged it. They will tighten the mounts on Thursday; use the bike until then.",
                    earliest.AddDays(-9).AddHours(3), now, sender: operators[0].Id, readAt: earliest.AddDays(-9).AddHours(4)));
                break;
            }

            case "k.sato":
            {
                var short1 = back[21];
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Report, "Short sleep, late shift",
                    "Only 5 hours after the late shift. Tired but fine.", short1.RecordedAt.AddMinutes(4), now, short1.Id,
                    readAt: short1.RecordedAt.AddHours(3)));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Re: Short sleep, late shift",
                    "Thanks. We moved your next shift. Aim for 7 hours and we will review in a week.",
                    short1.RecordedAt.AddHours(3), now, sender: surgeons[0].Id, readAt: short1.RecordedAt.AddHours(5)));

                // The sleep slide of the last two weeks: a read report and a reply he has not opened yet.
                var recent = window[^2];
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Report, "Waking up at night",
                    "Waking up several times a night for two weeks now. Sleeping about 4.5 hours.",
                    recent.RecordedAt.AddMinutes(4), now, recent.Id, readAt: recent.RecordedAt.AddHours(4)));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Re: Waking up at night",
                    "Noted. Avoid caffeine after 14:00 and keep the lights dim in the evening. Let's talk tomorrow at 10:00.",
                    recent.RecordedAt.AddHours(4), now, sender: surgeons[1].Id));

                // A solar particle event in the backfilled weeks, already handled.
                var speAt = earliest.AddDays(-25);
                var dose = new RadiationDose
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    RecordedAt = speAt,
                    ReceivedAt = Earlier(speAt.AddMinutes(5), now),
                    DoseMsv = 140m,
                    Type = RadiationType.Spe,
                    Note = "Solar particle event",
                };
                db.RadiationDoses.Add(dose);
                db.Alerts.Add(new Alert
                {
                    Id = Guid.CreateVersion7(),
                    AstronautId = astronautId,
                    Kind = AlertKind.Radiation,
                    RadiationDoseId = dose.Id,
                    Indicators = RadiationDoses.AlertSpe,
                    Message = RadiationDoses.SpeAlertMessage(dose.DoseMsv),
                    CreatedAt = dose.ReceivedAt!.Value,
                    AcknowledgedAt = Earlier(dose.ReceivedAt.Value.AddHours(1), now),
                    AcknowledgedByUserId = surgeons[0].Id,
                    AcknowledgeNote = "Crew sheltered, dose monitored",
                });
                break;
            }

            case "l.haddad":
            {
                var cold = back[24];
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Report, "Cold symptoms",
                    "Runny nose and a slight temperature (37.9 C). No other symptoms.", cold.RecordedAt.AddMinutes(3), now, cold.Id,
                    readAt: cold.RecordedAt.AddHours(2)));
                db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Re: Cold symptoms",
                    "Rest today, fluids, and keep taking your temperature morning and evening. Report if it goes above 38.5 C.",
                    cold.RecordedAt.AddHours(2), now, sender: surgeons[0].Id, readAt: cold.RecordedAt.AddHours(3)));
                break;
            }
        }
    }

    private static void AddOkoyeThread(
        AppDbContext db, Guid astronautId, Dictionary<int, CheckUp> checkUps, IReadOnlyList<PortalUser> surgeons, DateTimeOffset now)
    {
        var first = checkUps[1];
        db.Messages.Add(NewMessage(astronautId, MessageDirection.ToAstronaut, MessageKind.Message, "Welcome aboard",
            "Welcome to the crew, Daniel. Do your check-up every morning and send a report whenever something feels off.",
            first.RecordedAt.AddHours(-2), now, sender: surgeons[0].Id, readAt: first.RecordedAt.AddHours(-1)));

        // A report mission control has not answered yet: resting heart rate up and poor sleep in the last week.
        var recent = checkUps[58];
        db.Messages.Add(NewMessage(astronautId, MessageDirection.ToMissionControl, MessageKind.Report, "Resting heart rate elevated",
            "Resting heart rate is 104 for several days and I am sleeping about 5 hours. No other symptoms.",
            recent.RecordedAt.AddMinutes(4), now, recent.Id));
    }

    private static Message NewMessage(
        Guid astronautId, MessageDirection direction, MessageKind kind, string subject, string body, DateTimeOffset createdAt,
        DateTimeOffset now, Guid? checkUpId = null, Guid? sender = null, DateTimeOffset? readAt = null)
    {
        var created = Earlier(createdAt, now);
        return new Message
        {
            Id = Guid.CreateVersion7(),
            AstronautId = astronautId,
            Direction = direction,
            Kind = kind,
            CheckUpId = checkUpId,
            Subject = subject,
            Body = body,
            CreatedAt = created,
            ReceivedAt = created,
            ReadAt = readAt is null ? null : Earlier(readAt.Value, now),
            SenderUserId = sender,
        };
    }

    // ----- Time -----

    /// <summary>The most recent check-up time that is not in the future (today at the astronaut's usual hour, else yesterday).</summary>
    private static DateTimeOffset LastCheckUpTime(DateTimeOffset now, TimeOnly time)
    {
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.UtcDateTime).ToDateTime(time), TimeSpan.Zero);
        return today > now.AddMinutes(-10) ? today.AddDays(-1) : today;
    }

    private static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // ----- The stories of the added days (day 1 = oldest). Pure functions: no randomness. -----

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

    private static DayData Backfill(string userName, int day) => userName switch
    {
        "a.rivera" => Ana(day),
        "k.sato" => Kenji(day),
        _ => Leila(day),
    };

    // Ana, 31-60 days ago: a stressful week with a critical blood-pressure spike on day 11 (handled), then back to normal.
    private static DayData Ana(int day) => day switch
    {
        9 => Reads(146, 92, Jitter(66, 3, day, 3), Jitter(94, 5, day, 4), 36.6m, 6.4m, 3, "Stressful simulation week"),
        10 => Reads(144, 90, Jitter(66, 3, day, 3), Jitter(94, 5, day, 4), 36.6m, 6.1m, 3),
        11 => Reads(186, 98, 92, Jitter(96, 5, day, 4), 36.7m, 5.8m, 3, "Headache, BP high"),
        12 => Reads(138, 86, 70, Jitter(92, 5, day, 4), 36.6m, 6.6m, 4, "BP still a bit high"),
        13 => Reads(132, 82, 68, Jitter(92, 5, day, 4), 36.6m, 7.0m, 4),
        _ => Reads(
            Jitter(118, 4, day, 1), Jitter(76, 3, day, 2), Jitter(64, 4, day, 3), Jitter(92, 6, day, 4),
            Jitter(36.6m, 0.2, day, 5, 1), Jitter(7.2m, 0.5, day, 6, 1), Wave(day, 7) > 0.6 ? 5 : 4),
    };

    // Kenji, 31-60 days ago: a post-meal sugar reading and a short-sleep week after late shifts, then recovery.
    private static DayData Kenji(int day) => day switch
    {
        5 => Reads(Jitter(112, 4, day, 1), Jitter(72, 3, day, 2), Jitter(58, 4, day, 3), 148, Jitter(36.5m, 0.2, day, 5, 1), 7.0m, 4, "Post-meal reading"),
        20 => Reads(Jitter(114, 4, day, 1), Jitter(73, 3, day, 2), Jitter(60, 4, day, 3), Jitter(88, 6, day, 4), 36.5m, 5.5m, 3),
        21 => Reads(Jitter(116, 4, day, 1), Jitter(74, 3, day, 2), Jitter(62, 4, day, 3), Jitter(88, 6, day, 4), 36.5m, 5.2m, 2, "Late shift, short sleep"),
        22 => Reads(Jitter(114, 4, day, 1), Jitter(73, 3, day, 2), Jitter(61, 4, day, 3), Jitter(88, 6, day, 4), 36.5m, 5.6m, 3),
        23 => Reads(Jitter(112, 4, day, 1), Jitter(72, 3, day, 2), Jitter(58, 4, day, 3), Jitter(88, 6, day, 4), 36.5m, 6.5m, 4),
        _ => Reads(
            Jitter(112, 4, day, 1), Jitter(72, 3, day, 2), Jitter(58, 4, day, 3), Jitter(88, 6, day, 4),
            Jitter(36.5m, 0.2, day, 5, 1), Jitter(7.0m, 0.4, day, 6, 1), 4),
    };

    // Leila, 31-60 days ago: a raised pulse after EVA training and a mild cold.
    private static DayData Leila(int day) => day switch
    {
        14 => Reads(Jitter(108, 4, day, 1), Jitter(68, 3, day, 2), 108, Jitter(85, 6, day, 4), 36.8m, 7.0m, 4, "After EVA training"),
        24 => Reads(Jitter(108, 4, day, 1), Jitter(68, 3, day, 2), 94, Jitter(85, 6, day, 4), 37.9m, 6.6m, 3, "Mild cold symptoms"),
        25 => Reads(Jitter(108, 4, day, 1), Jitter(68, 3, day, 2), 88, Jitter(85, 6, day, 4), 37.7m, 6.8m, 3),
        26 => Reads(Jitter(108, 4, day, 1), Jitter(68, 3, day, 2), 76, Jitter(85, 6, day, 4), 37.3m, 7.2m, 4),
        _ => Reads(
            Jitter(108, 4, day, 1), Jitter(68, 3, day, 2), Jitter(68, 4, day, 3), Jitter(85, 6, day, 4),
            Jitter(36.7m, 0.2, day, 5, 1), Jitter(7.5m, 0.4, day, 6, 1), 4),
    };

    // Daniel, 60 days (day 60 = most recent): healthy, one high-sugar reading, then a week of high pulse and poor sleep.
    private static DayData Okoye(int day) => day switch
    {
        40 => Reads(Jitter(124, 4, day, 1), Jitter(78, 3, day, 2), Jitter(70, 4, day, 3), 152, 36.6m, 6.8m, 4, "Skipped breakfast"),
        >= 54 => Reads(
            Jitter(128, 3, day, 1), Jitter(82, 2, day, 2), 100 + (day - 54), Jitter(96, 5, day, 4), 36.7m,
            decimal.Round(5.4m + 0.1m * (day % 3), 1), 3,
            day == 58 ? "Resting heart rate elevated" : day == 60 ? "Still sleeping poorly" : null),
        _ => Reads(
            Jitter(124, 4, day, 1), Jitter(78, 3, day, 2), Jitter(70, 4, day, 3), Jitter(95, 6, day, 4),
            Jitter(36.6m, 0.2, day, 5, 1), Jitter(6.8m, 0.5, day, 6, 1), Wave(day, 7) > 0.5 ? 5 : 4),
    };
}
