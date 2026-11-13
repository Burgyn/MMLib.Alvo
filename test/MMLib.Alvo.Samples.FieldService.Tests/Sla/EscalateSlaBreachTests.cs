using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.TestHost;

namespace MMLib.Alvo.Samples.FieldService.Tests.Sla;

public sealed class EscalateSlaBreachTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_emergency_nobody_scheduled_by_its_respond_by_time_goes_to_priority_one_and_pages_on_call()
    {
        var (time, notifier) = (new FakeTimeProvider(FieldServiceWorld.Now), new RecordingNotifier());
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, time, notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var job = await alvo.CreateJobAsync(north, "WO-7101", Ct, isEmergency: true, respondBy: FieldServiceWorld.Now.AddHours(2));

        time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        (await PriorityAsync(alvo, north, job)).ShouldBe(3);
        notifier.Pages.ShouldBeEmpty();

        time.Advance(TimeSpan.FromHours(1));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        (await PriorityAsync(alvo, north, job)).ShouldBe(1);
        notifier.Pages.ShouldHaveSingleItem().ShouldBe(new(job.Id, "WO-7101", "JOB-2026-00001", FieldServiceWorld.Now.AddHours(2)));
    }

    [Fact]
    public async Task An_emergency_scheduled_before_its_deadline_is_never_escalated()
    {
        var (time, notifier) = (new FakeTimeProvider(FieldServiceWorld.Now), new RecordingNotifier());
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, time, notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var job = await alvo.CreateJobAsync(north, "WO-7102", Ct, isEmergency: true, respondBy: FieldServiceWorld.Now.AddHours(2));

        await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant)).UpdateAsync(job.Id, WorkOrder.Patch()
            .SetAssignedTo(FieldServiceWorld.Technician)
            .SetScheduledFor(FieldServiceWorld.Now.AddHours(1))
            .SetStatus(WorkOrderStatus.Scheduled), cancellationToken: Ct);
        time.Advance(TimeSpan.FromHours(3));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        (await PriorityAsync(alvo, north, job)).ShouldBe(3, "the write that scheduled it made the timer's `when` false, and cancelled it");
        notifier.Pages.ShouldBeEmpty();
    }

    [Fact]
    public async Task Moving_respond_by_later_moves_the_escalation_with_it()
    {
        var (time, notifier) = (new FakeTimeProvider(FieldServiceWorld.Now), new RecordingNotifier());
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, time, notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var job = await alvo.CreateJobAsync(north, "WO-7103", Ct, isEmergency: true, respondBy: FieldServiceWorld.Now.AddHours(1));

        await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant))
            .UpdateAsync(job.Id, WorkOrder.Patch().SetRespondBy(FieldServiceWorld.Now.AddHours(4)), cancellationToken: Ct);
        time.Advance(TimeSpan.FromHours(2));
        await alvo.DrainEventsAsync(Ct);
        notifier.Pages.ShouldBeEmpty("the deadline moved; the timer moved with it, in the same write");

        time.Advance(TimeSpan.FromHours(2));
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        notifier.Pages.ShouldHaveSingleItem().RespondBy.ShouldBe(FieldServiceWorld.Now.AddHours(4));
    }

    private static async Task<long> PriorityAsync(AlvoTestHost alvo, Seeded seed, WorkOrder job) =>
        (await alvo.Session(FieldServiceWorld.Dispatcher(seed.Tenant)).GetAsync<WorkOrder>(job.Id, Ct))!.Priority;
}
