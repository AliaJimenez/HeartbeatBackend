using Heartbeat.Shared;

namespace Heartbeat.Tests;

public class CheckUpStreaksTests
{
    // "Today" is 2026-11-14 at 12:00 UTC.
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private static DateTimeOffset Day(int daysAgo, int hour = 9) => new DateTimeOffset(2026, 11, 14, hour, 0, 0, TimeSpan.Zero).AddDays(-daysAgo);

    private static StreakSummary Calc(params int[] daysAgo) => CheckUpStreaks.Calculate(daysAgo.Select(d => Day(d)), Now);

    [Fact]
    public void No_check_ups_means_no_streak()
    {
        var s = CheckUpStreaks.Calculate([], Now);

        Assert.Equal((0, 0, false, 0, StreakBadge.None), (s.Current, s.Best, s.CompletedToday, s.TotalDays, s.Badge));
        Assert.Null(s.LastCheckUpDay);
        Assert.Equal(3, s.DaysToNextBadge);
    }

    [Fact]
    public void A_check_up_today_starts_a_streak_of_one()
    {
        var s = Calc(0);

        Assert.Equal((1, 1, true, 1), (s.Current, s.Best, s.CompletedToday, s.TotalDays));
        Assert.Equal(new DateOnly(2026, 11, 14), s.LastCheckUpDay);
    }

    [Fact]
    public void Consecutive_days_ending_today_count_in_full()
    {
        var s = Calc(0, 1, 2, 3, 4);

        Assert.Equal((5, 5, true), (s.Current, s.Best, s.CompletedToday));
    }

    [Fact]
    public void The_streak_survives_until_the_end_of_today_when_yesterday_was_done()
    {
        var s = Calc(1, 2, 3);

        Assert.Equal(3, s.Current);          // still alive: today's check-up is still to do
        Assert.False(s.CompletedToday);
        Assert.Equal(StreakBadge.Bronze, s.Badge);
    }

    [Fact]
    public void Missing_a_whole_day_resets_the_current_streak_but_keeps_the_best()
    {
        var s = Calc(2, 3, 4, 5);            // nothing yesterday or today

        Assert.Equal(0, s.Current);
        Assert.Equal(4, s.Best);
        Assert.False(s.CompletedToday);
        Assert.Equal(StreakBadge.None, s.Badge);
    }

    [Fact]
    public void Best_streak_can_be_older_than_the_current_one()
    {
        // Today and yesterday (2), a gap, then an older run of 4.
        var s = Calc(0, 1, 3, 4, 5, 6, 10);

        Assert.Equal(2, s.Current);
        Assert.Equal(4, s.Best);
        Assert.Equal(7, s.TotalDays);
    }

    [Fact]
    public void Several_check_ups_on_one_day_count_once()
    {
        var s = CheckUpStreaks.Calculate([Day(0, 7), Day(0, 9), Day(0, 11), Day(1, 8)], Now);

        Assert.Equal((2, 2, 2), (s.Current, s.Best, s.TotalDays));
    }

    [Fact]
    public void Input_order_and_duplicates_do_not_matter()
    {
        var a = Calc(0, 1, 2, 5, 6);
        var b = Calc(6, 2, 0, 5, 1, 1, 0);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Future_dated_check_ups_cannot_extend_a_streak()
    {
        // A laptop whose clock ran ahead recorded tomorrow and the day after.
        var s = CheckUpStreaks.Calculate([Day(0), Day(-1), Day(-2)], Now);

        Assert.Equal((1, 1, 1), (s.Current, s.Best, s.TotalDays));
    }

    [Fact]
    public void Midnight_boundary_separates_days_in_utc()
    {
        var late = new DateTimeOffset(2026, 11, 13, 23, 59, 59, TimeSpan.Zero);
        var early = new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero);

        Assert.Equal(2, CheckUpStreaks.Calculate([late, early], Now).Current);
        Assert.Equal(1, CheckUpStreaks.Calculate([late, late.AddMinutes(-5)], Now.AddDays(-1)).Current);
    }

    [Fact]
    public void Days_follow_the_astronauts_time_zone()
    {
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("jst", TimeSpan.FromHours(9), "JST", "JST");

        // 22:00 UTC on the 13th and 01:00 UTC on the 14th are both the 14th in Tokyo (07:00 and 10:00)... one day.
        var s = CheckUpStreaks.Calculate(
            [new DateTimeOffset(2026, 11, 13, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero)],
            Now, tokyo);
        Assert.Equal((1, 1), (s.Current, s.TotalDays));

        // The same two instants are two different days in UTC.
        Assert.Equal(2, CheckUpStreaks.Calculate(
            [new DateTimeOffset(2026, 11, 13, 22, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 14, 1, 0, 0, TimeSpan.Zero)], Now).Current);
    }

    [Fact]
    public void Offsets_on_the_input_are_converted_before_picking_the_day()
    {
        // 2026-11-14 00:30 at UTC+2 is 2026-11-13 22:30 UTC.
        var s = CheckUpStreaks.Calculate([new DateTimeOffset(2026, 11, 14, 0, 30, 0, TimeSpan.FromHours(2))], Now);

        Assert.Equal(new DateOnly(2026, 11, 13), s.LastCheckUpDay);
        Assert.False(s.CompletedToday);
    }

    [Theory]
    [InlineData(0, StreakBadge.None, 3)]
    [InlineData(2, StreakBadge.None, 1)]
    [InlineData(3, StreakBadge.Bronze, 4)]
    [InlineData(6, StreakBadge.Bronze, 1)]
    [InlineData(7, StreakBadge.Silver, 23)]
    [InlineData(29, StreakBadge.Silver, 1)]
    [InlineData(30, StreakBadge.Gold, 0)]
    [InlineData(365, StreakBadge.Gold, 0)]
    public void Badge_thresholds_and_days_to_the_next_one(int days, StreakBadge badge, int toNext)
    {
        Assert.Equal(badge, CheckUpStreaks.BadgeFor(days));
        Assert.Equal(toNext, CheckUpStreaks.DaysToNextBadge(days));
    }

    [Fact]
    public void A_thirty_day_run_earns_gold_and_the_summary_carries_the_badge()
    {
        var s = CheckUpStreaks.Calculate(Enumerable.Range(0, 30).Select(d => Day(d)), Now);

        Assert.Equal((30, 30, StreakBadge.Gold, 0), (s.Current, s.Best, s.Badge, s.DaysToNextBadge));
    }

    [Fact]
    public void The_badge_follows_the_current_streak_not_the_best()
    {
        // A 10-day run long ago, nothing in the last week.
        var s = Calc(Enumerable.Range(10, 10).ToArray());

        Assert.Equal((0, 10, StreakBadge.None), (s.Current, s.Best, s.Badge));
    }

    [Fact]
    public void Month_and_year_boundaries_are_continuous()
    {
        var newYear = new DateTimeOffset(2027, 1, 1, 10, 0, 0, TimeSpan.Zero);
        var s = CheckUpStreaks.Calculate(
            [new DateTimeOffset(2026, 12, 30, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 31, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2027, 1, 1, 9, 0, 0, TimeSpan.Zero)],
            newYear);

        Assert.Equal((3, StreakBadge.Bronze), (s.Current, s.Badge));
    }

    [Fact]
    public void A_daylight_saving_change_does_not_break_a_streak()
    {
        // Berlin switched to winter time on 2026-10-25 (a 25-hour day).
        var berlin = TimeZoneInfo.FindSystemTimeZoneById(OperatingSystem.IsWindows() ? "W. Europe Standard Time" : "Europe/Berlin");
        var utcTimes = new[] { 24, 25, 26 }.Select(d => new DateTimeOffset(2026, 10, d, 7, 30, 0, TimeSpan.Zero)).ToList();

        var s = CheckUpStreaks.Calculate(utcTimes, new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero), berlin);

        Assert.Equal(3, s.Current);
    }
}
