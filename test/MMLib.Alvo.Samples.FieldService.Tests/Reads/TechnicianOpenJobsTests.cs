using MMLib.Alvo.Data;
using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Tests.Reads;

public sealed class TechnicianOpenJobsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_technicians_list_shows_their_open_jobs_and_the_dispatchers_shows_every_job()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        await alvo.CreateJobAsync(north, "WO-7301", Ct, WorkOrderStatus.Scheduled, scheduledFor: FieldServiceWorld.Now.AddDays(1), assignedTo: FieldServiceWorld.Technician);
        await alvo.CreateJobAsync(north, "WO-7302", Ct, WorkOrderStatus.Completed, assignedTo: FieldServiceWorld.Technician);

        var technicians = await ReferencesAsync(alvo, FieldServiceWorld.TechnicianIn(north.Tenant));
        var dispatchers = await ReferencesAsync(alvo, FieldServiceWorld.Dispatcher(north.Tenant));

        technicians.ShouldBe(["WO-7301"]);
        dispatchers.ShouldBe(["WO-7301", "WO-7302"], ignoreOrder: true);
    }

    [Fact]
    public async Task The_narrowing_applies_to_a_get_too_so_a_finished_job_reads_as_absent_for_the_technician()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var done = await alvo.CreateJobAsync(north, "WO-7302", Ct, WorkOrderStatus.Completed, assignedTo: FieldServiceWorld.Technician);

        var asTechnician = await alvo.Session(FieldServiceWorld.TechnicianIn(north.Tenant)).GetAsync<WorkOrder>(done.Id, Ct);
        var asDispatcher = await alvo.Session(FieldServiceWorld.Dispatcher(north.Tenant)).GetAsync<WorkOrder>(done.Id, Ct);

        asTechnician.ShouldBeNull("reads.mdx: a narrowing runs once per list, get or count");
        asDispatcher.ShouldNotBeNull();
    }

    private static async Task<IReadOnlyList<string>> ReferencesAsync(MMLib.Alvo.TestHost.AlvoTestHost alvo, AlvoContext caller) =>
        [.. (await alvo.Session(caller).QueryAsync<WorkOrder>(x => x.Reference != "", limit: 50, Ct)).Select(job => job.Reference)];
}
