using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Scheduling;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.Samples.FieldService.Tests.Scheduling;

public sealed class VisitWithinServiceHoursTests
{
    /// <summary>Monday 16 November 2026, 18:30 in Bratislava (CET, UTC+1).</summary>
    private static readonly DateTimeOffset _halfPastSix = new(2026, 11, 16, 17, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _ten = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_visit_inside_the_hours_is_booked()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var (north, job) = await JobAsync(alvo);

        var visit = await BookAsync(alvo, north, job, _ten, minutes: 120);

        visit.EndsAt.ShouldBe(_ten.AddMinutes(120));
    }

    [Fact]
    public async Task A_visit_that_starts_in_time_but_ends_after_closing_is_refused_at_ends_at()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var (north, job) = await JobAsync(alvo);

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(() => BookAsync(alvo, north, job, _halfPastSix, minutes: 120));

        (refusal.Kind, refusal.Code, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Invalid, "outside-service-hours", "/ends_at"));
    }

    [Fact]
    public async Task Moving_a_booked_visit_to_three_in_the_morning_is_refused_at_starts_at_and_the_slot_stays()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var (north, job) = await JobAsync(alvo);
        var visit = await BookAsync(alvo, north, job, _ten, minutes: 60);
        var threeInTheMorning = new DateTimeOffset(2026, 11, 16, 2, 0, 0, TimeSpan.Zero);
        var dispatcher = alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant));

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(() => dispatcher.UpdateAsync(
            visit.Id, Visit.Patch().SetStartsAt(threeInTheMorning).SetEndsAt(threeInTheMorning.AddHours(1)), cancellationToken: Ct));

        (refusal.Code, refusal.Pointer).ShouldBe(("outside-service-hours", "/starts_at"));
        (await dispatcher.GetAsync<Visit>(visit.Id, Ct))!.StartsAt.ShouldBe(_ten);
    }

    [Fact]
    public async Task Recording_the_minutes_on_a_visit_keeps_its_slot_and_is_not_judged_again()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var (north, job) = await JobAsync(alvo);
        var visit = await BookAsync(alvo, north, job, _ten, minutes: 60);
        var dispatcher = alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant));
        await dispatcher.UpdateAsync(north.CustomerId, Customer.Patch().SetServiceOpensHour(12), cancellationToken: Ct);

        var recorded = await dispatcher.UpdateAsync(visit.Id, Visit.Patch().SetMinutesOnSite(55), cancellationToken: Ct);

        recorded!.MinutesOnSite.ShouldBe(55);
    }

    [Theory]
    [InlineData(7, 16, 30, "ends_at")]
    [InlineData(11, 16, 30, null)]
    public void The_same_utc_slot_is_judged_on_the_local_clock_so_daylight_saving_changes_the_verdict(int month, int utcHour, int minutes, string? broken)
    {
        var start = new DateTimeOffset(2026, month, 16, utcHour, minutes, 0, TimeSpan.Zero);

        ServiceHours.Violation(8, 18, start, start.AddMinutes(30), "Europe/Bratislava").ShouldBe(broken);
    }

    [Fact]
    public void A_slot_running_past_midnight_is_refused_when_only_an_opening_hour_is_set()
    {
        var lateEvening = new DateTimeOffset(2026, 11, 16, 22, 0, 0, TimeSpan.Zero);

        ServiceHours.Violation(8, null, lateEvening, lateEvening.AddHours(2), "Europe/Bratislava").ShouldBe("ends_at");
        ServiceHours.Violation(null, null, lateEvening, lateEvening.AddHours(2), "Europe/Bratislava").ShouldBeNull();
    }

    private static async Task<(Seeded North, WorkOrder Job)> JobAsync(AlvoTestHost alvo)
    {
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct, opensHour: 7, closesHour: 19);
        return (north, await alvo.CreateJobAsync(north, "WO-7001", Ct));
    }

    private static async Task<Visit> BookAsync(AlvoTestHost alvo, Seeded north, WorkOrder job, DateTimeOffset startsAt, int minutes) =>
        (await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant)).CreateAsync(
            Visit.Create(job.Id, FieldServiceWorld.Technician, startsAt, startsAt.AddMinutes(minutes), tenantId: north.Tenant.Value),
            Ct))!;
}
