using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;
using MMLib.Alvo.Samples.FieldService.Notifications;

namespace MMLib.Alvo.Samples.FieldService.Tests.Notifications;

public sealed class NotifyCustomerOfVisitTests
{
    private static readonly DateTimeOffset _tenInBratislava = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Scheduling_a_job_tells_its_customer_when_the_technician_is_coming_once_after_the_commit()
    {
        var notifier = new RecordingNotifier();
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, notifier: notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);

        await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant)).UpdateAsync(job.Id, Scheduling(), cancellationToken: Ct);
        notifier.Visits.ShouldBeEmpty("a handler runs after the commit, when the outbox is drained — not inside the write");
        await alvo.DrainEventsAsync(Ct);

        notifier.Visits.ShouldBe([new VisitNotice(job.Id, "WO-7001", "JOB-2026-00001", "wo-7001@example.com", _tenInBratislava)]);
    }

    [Fact]
    public async Task An_update_that_moves_no_status_tells_the_customer_nothing()
    {
        var notifier = new RecordingNotifier();
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, notifier: notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);

        await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant))
            .UpdateAsync(job.Id, WorkOrder.Patch().SetPriority(1), cancellationToken: Ct);
        await alvo.DrainEventsAsync(Ct);

        notifier.Visits.ShouldBeEmpty();
    }

    private static WorkOrderPatch Scheduling() => WorkOrder.Patch()
        .SetAssignedTo(FieldServiceWorld.Technician)
        .SetScheduledFor(_tenInBratislava)
        .SetStatus(WorkOrderStatus.Scheduled);
}
