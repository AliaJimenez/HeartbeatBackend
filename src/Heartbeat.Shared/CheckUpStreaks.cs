namespace Heartbeat.Shared;

/// <summary>Simple dashboard badge earned by the current daily check-up streak (NR-06).</summary>
public enum StreakBadge
{
    None = 0,
    Bronze = 1,   // 3 days in a row
    Silver = 2,   // 7 days
    Gold = 3,     // 30 days
}

/// <summary>
/// Streak figures for one astronaut. <see cref="Current"/> is the run of consecutive days with at least one check-up
/// that is still alive: it ends today, or yesterday when today's check-up is still to do
/// (<see cref="CompletedToday"/> is false), and is 0 once a whole day has been missed.
/// </summary>
public sealed record StreakSummary(
    int Current,
    int Best,
    bool CompletedToday,
    DateOnly? LastCheckUpDay,
    int TotalDays,
    StreakBadge Badge,
    int DaysToNextBadge);

/// <summary>
/// Daily check-up streaks. Pure and deterministic, so the desktop (offline, from its local SQLite) and the server
/// compute exactly the same numbers. Days are calendar days in the astronaut's time zone, because that is what a
/// "daily routine" means to them; timestamps stay UTC everywhere else (CONTRACTS.md §1).
/// </summary>
public static class CheckUpStreaks
{
    public const int BronzeDays = 3;
    public const int SilverDays = 7;
    public const int GoldDays = 30;

    /// <param name="recordedAt">When each check-up was saved. Pass only check-ups that still count (not superseded
    /// ones if you want corrections to be ignored); several on the same day count once.</param>
    /// <param name="now">The current instant (inject it in tests).</param>
    /// <param name="timeZone">The astronaut's time zone; UTC when null.</param>
    public static StreakSummary Calculate(IEnumerable<DateTimeOffset> recordedAt, DateTimeOffset now, TimeZoneInfo? timeZone = null)
    {
        var zone = timeZone ?? TimeZoneInfo.Utc;
        var today = DayOf(now, zone);

        // Check-ups dated after "today" (a laptop clock that ran ahead) cannot extend a streak.
        var days = recordedAt.Select(t => DayOf(t, zone)).Where(d => d <= today).ToHashSet();
        if (days.Count == 0)
        {
            return new StreakSummary(0, 0, false, null, 0, StreakBadge.None, BronzeDays);
        }

        var completedToday = days.Contains(today);
        var current = RunEndingAt(days, completedToday ? today : today.AddDays(-1));
        var best = BestRun(days);
        var badge = BadgeFor(current);

        return new StreakSummary(current, best, completedToday, days.Max(), days.Count, badge, DaysToNextBadge(current));
    }

    public static StreakBadge BadgeFor(int streakDays) => streakDays switch
    {
        >= GoldDays => StreakBadge.Gold,
        >= SilverDays => StreakBadge.Silver,
        >= BronzeDays => StreakBadge.Bronze,
        _ => StreakBadge.None,
    };

    /// <summary>Days still needed to reach the next badge; 0 once the top badge is earned.</summary>
    public static int DaysToNextBadge(int streakDays) => streakDays switch
    {
        >= GoldDays => 0,
        >= SilverDays => GoldDays - streakDays,
        >= BronzeDays => SilverDays - streakDays,
        _ => BronzeDays - streakDays,
    };

    private static DateOnly DayOf(DateTimeOffset instant, TimeZoneInfo zone)
        => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    private static int RunEndingAt(HashSet<DateOnly> days, DateOnly last)
    {
        var run = 0;
        for (var d = last; days.Contains(d); d = d.AddDays(-1))
        {
            run++;
        }

        return run;
    }

    private static int BestRun(HashSet<DateOnly> days)
    {
        var best = 0;
        foreach (var start in days.Where(d => !days.Contains(d.AddDays(-1))))
        {
            var run = 0;
            for (var d = start; days.Contains(d); d = d.AddDays(1))
            {
                run++;
            }

            best = Math.Max(best, run);
        }

        return best;
    }
}
