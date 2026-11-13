using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Scheduling;

namespace MMLib.Alvo.Samples.FieldService.Tests.Scheduling;

public sealed class WithinServiceHoursTests
{
    private static readonly DateTimeOffset _nineInBratislava = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _halfPastSixInBratislava = new(2026, 11, 16, 17, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_slot_inside_the_customers_service_hours_is_stored()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct, opensHour: 8, closesHour: 17);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);

        var scheduled = await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant))
            .UpdateAsync(job.Id, WorkOrder.Patch().SetScheduledFor(_nineInBratislava), cancellationToken: Ct);

        scheduled!.ScheduledFor.ShouldBe(_nineInBratislava);
    }

    [Fact]
    public async Task A_slot_after_closing_is_refused_at_scheduled_for_and_nothing_is_written()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct, opensHour: 8, closesHour: 17);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);
        var dispatcher = alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant));

        var refusal = await Should.ThrowAsync<AlvoWriteRejectedException>(
            () => dispatcher.UpdateAsync(job.Id, WorkOrder.Patch().SetScheduledFor(_halfPastSixInBratislava), cancellationToken: Ct));

        (refusal.Kind, refusal.Code, refusal.Pointer).ShouldBe((AlvoWriteRejectionKind.Invalid, "outside-service-hours", "/scheduled_for"));
        (await dispatcher.GetAsync<WorkOrder>(job.Id, Ct))!.ScheduledFor.ShouldBeNull();
    }

    [Fact]
    public async Task A_customer_with_no_service_hours_accepts_any_slot()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct, opensHour: null, closesHour: null);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);

        var scheduled = await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant))
            .UpdateAsync(job.Id, WorkOrder.Patch().SetScheduledFor(_halfPastSixInBratislava), cancellationToken: Ct);

        scheduled!.ScheduledFor.ShouldBe(_halfPastSixInBratislava);
    }

    [Fact]
    public async Task An_update_that_keeps_the_slot_is_not_judged_again()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct, opensHour: 8, closesHour: 17);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct, scheduledFor: _nineInBratislava);
        var dispatcher = alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant));
        await dispatcher.UpdateAsync(north.CustomerId, Customer.Patch().SetServiceOpensHour(10), cancellationToken: Ct);

        var touched = await dispatcher.UpdateAsync(job.Id, WorkOrder.Patch().SetPriority(2), cancellationToken: Ct);

        touched!.Priority.ShouldBe(2);
        touched.ScheduledFor.ShouldBe(_nineInBratislava, "a slot booked before the hours changed stays bookable for other edits");
    }

    [Theory]
    [InlineData(7, false)]
    [InlineData(8, true)]
    [InlineData(16, true)]
    [InlineData(17, false)]
    public void The_local_hour_is_judged_against_opening_inclusive_and_closing_exclusive(int localHour, bool admitted)
    {
        var at = new DateTimeOffset(2026, 7, 15, localHour, 0, 0, TimeSpan.FromHours(2));

        ServiceHours.Admit(8, 17, at, "Europe/Bratislava").ShouldBe(admitted);
    }
}
