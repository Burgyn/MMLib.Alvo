using MMLib.Alvo.Samples.FieldService.Model;

namespace MMLib.Alvo.Samples.FieldService.Tests;

public sealed class BootTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_shared_descriptor_boots_with_every_slice_and_a_typed_create_gets_its_job_number()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);

        var job = await alvo.CreateJobAsync(north, "WO-7001", Ct);

        (job.Reference, job.Status, job.JobNumber).ShouldBe(("WO-7001", WorkOrderStatus.New, "JOB-2026-00001"));
    }

    [Fact]
    public async Task Each_tenant_counts_its_own_job_numbers()
    {
        await using var alvo = await FieldServiceWorld.StartAsync(Ct);
        var north = await alvo.SeedAsync(FieldServiceWorld.North, Ct);
        var south = await alvo.SeedAsync(FieldServiceWorld.South, Ct);

        await alvo.CreateJobAsync(north, "WO-7001", Ct);
        var second = await alvo.CreateJobAsync(north, "WO-7002", Ct);
        var southFirst = await alvo.CreateJobAsync(south, "WO-7003", Ct);

        (second.JobNumber, southFirst.JobNumber).ShouldBe(("JOB-2026-00002", "JOB-2026-00001"));
    }
}
