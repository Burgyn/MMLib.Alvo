using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Tests.Scheduling;

/// <summary>
/// The cron a schedule runs on: Cronos's documented daylight-saving behaviour, pinned in Bratislava, and every malformed
/// input answered as a problem rather than thrown.
/// </summary>
public sealed class CronScheduleTests
{
    private static readonly DateTimeOffset _now = Utc(2026, 11, 6, 9, 0);

    [Fact]
    public void Bratislava_spring_forward_runs_a_skipped_time_at_the_end_of_the_gap()
    {
        var schedule = Parsed("0 2 * * *", "Europe/Bratislava");

        schedule.Next(Utc(2026, 3, 28, 12, 0)).ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.FromHours(2)));
        schedule.Next(Utc(2026, 3, 29, 1, 0)).ShouldBe(new DateTimeOffset(2026, 3, 30, 2, 0, 0, TimeSpan.FromHours(2)));
    }

    [Fact]
    public void Bratislava_fall_back_runs_a_fixed_time_once()
    {
        var schedule = Parsed("30 2 * * *", "Europe/Bratislava");

        var first = schedule.Next(Utc(2026, 10, 24, 12, 0))!.Value;
        first.ShouldBe(new DateTimeOffset(2026, 10, 25, 2, 30, 0, TimeSpan.FromHours(2)));
        schedule.Next(first).ShouldBe(new DateTimeOffset(2026, 10, 26, 2, 30, 0, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Bratislava_fall_back_runs_an_hourly_schedule_in_both_two_oclocks()
    {
        var schedule = Parsed("0 * * * *", "Europe/Bratislava");
        var from = Utc(2026, 10, 24, 23, 30);

        Occurrences(schedule, from, 3).ShouldBe([Utc(2026, 10, 25, 0, 0), Utc(2026, 10, 25, 1, 0), Utc(2026, 10, 25, 2, 0)]);
    }

    [Fact]
    public void The_next_occurrence_is_exclusive_and_in_utc()
    {
        var schedule = Parsed("0 2 * * *", "UTC");

        var next = schedule.Next(Utc(2026, 11, 6, 2, 0))!.Value;

        next.ShouldBe(Utc(2026, 11, 7, 2, 0));
        next.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Theory]
    [InlineData("0 0 2 * * *", "UTC", "cron")]
    [InlineData("0 2 * * *", "Mars/Olympus_Mons", "time zone")]
    [InlineData("every night", "UTC", "cron")]
    [InlineData("61 2 * * *", "UTC", "cron")]
    public void A_seconds_field_an_unknown_zone_and_a_malformed_cron_are_problems_not_exceptions(string cron, string zone, string named)
    {
        CronSchedule.TryParse(cron, zone, _now, out var schedule, out var problem).ShouldBeFalse();

        schedule.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain(named);
    }

    [Fact]
    public void A_cron_with_no_next_occurrence_is_a_problem()
    {
        CronSchedule.TryParse("0 0 30 2 *", "UTC", _now, out var schedule, out var problem).ShouldBeFalse();

        schedule.ShouldBeNull();
        problem.ShouldNotBeNull().ShouldContain("never");
    }

    private static CronSchedule Parsed(string cron, string zone)
    {
        CronSchedule.TryParse(cron, zone, _now, out var schedule, out var problem).ShouldBeTrue(problem);

        return schedule!;
    }

    private static List<DateTimeOffset> Occurrences(CronSchedule schedule, DateTimeOffset from, int count)
    {
        var found = new List<DateTimeOffset>();
        var after = from;
        while (found.Count < count && schedule.Next(after) is { } next)
        {
            found.Add(next);
            after = next;
        }

        return found;
    }

    private static DateTimeOffset Utc(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);
}
