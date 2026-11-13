using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Tests.Reports;

public sealed class OverdueReportTests
{
    /// <summary>Saturday 14 November 2026, 06:00 in Bratislava.</summary>
    private static readonly DateTimeOffset _sixNextMorning = new(2026, 11, 14, 5, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task At_six_in_Bratislava_each_tenant_gets_one_report_naming_only_its_own_overdue_jobs()
    {
        var (time, notifier) = (new FakeTimeProvider(FieldServiceWorld.Now), new RecordingNotifier());
        await using var alvo = await FieldServiceWorld.StartAsync(Ct, time, notifier);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var south = await alvo.SeedAsync(FieldServiceWorld.South, Ct);
        await alvo.CreateJobAsync(north, "WO-7201", Ct, WorkOrderStatus.Scheduled, scheduledFor: FieldServiceWorld.Now.AddDays(-1), assignedTo: FieldServiceWorld.Technician);
        await alvo.CreateJobAsync(north, "WO-7202", Ct, WorkOrderStatus.Scheduled, scheduledFor: FieldServiceWorld.Now.AddDays(2), assignedTo: FieldServiceWorld.Technician);
        await alvo.CreateJobAsync(north, "WO-7204", Ct, WorkOrderStatus.InProgress, scheduledFor: FieldServiceWorld.Now.AddDays(-1), assignedTo: FieldServiceWorld.Technician);
        await alvo.CreateJobAsync(south, "WO-7203", Ct, isEmergency: true, respondBy: FieldServiceWorld.Now.AddHours(-1));
        await alvo.DrainEventsAsync(Ct);
        notifier.Reports.ShouldBeEmpty("the report runs at six, not when the host starts");

        time.Advance(_sixNextMorning - FieldServiceWorld.Now);
        await alvo.DrainEventsAsync(Ct);
        await alvo.DrainEventsAsync(Ct);

        notifier.Reports.Select(report => (report.Tenant, report.At, string.Join(",", report.References)))
            .ShouldBe(
                [
                    (FieldServiceWorld.North.Value, _sixNextMorning, "WO-7201"),
                    (FieldServiceWorld.South.Value, _sixNextMorning, "WO-7203"),
                ],
                ignoreOrder: true);
    }
}
