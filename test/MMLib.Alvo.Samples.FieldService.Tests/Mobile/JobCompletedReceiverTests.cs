using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Samples.FieldService.Tests.Mobile;

public sealed class JobCompletedReceiverTests : IAsyncLifetime
{
    private const string Route = "/hooks/technician-app";
    private static readonly DateTimeOffset _tenInBratislava = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private HttpWorld _world = null!;
    private (Guid Region, Guid Customer) _seed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await HttpWorld.StartAsync(Ct);
        _seed = await _world.SeedAsync();
    }

    [Fact]
    public async Task A_signed_completion_of_a_job_in_progress_completes_it_and_records_the_minutes_on_its_visit()
    {
        var job = await DispatchedJobAsync("WO-7001");
        using var started = await _world.SendAsync($"/api/work_orders/{job}", "dispatcher", HttpMethod.Patch, new { status = "in_progress" });

        using var completed = await _world.SignedAsync(Route, "msg_1", Notice(job));

        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var stored = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");
        (stored["status"]!.GetValue<string>(), stored["completed_on"]!.GetValue<string>(), stored["on_site_minutes"]!.GetValue<long>())
            .ShouldBe(("completed", "2026-11-16", 95L));
        (await VisitOfAsync(job))["minutes_on_site"]!.GetValue<long>().ShouldBe(95L);
    }

    [Fact]
    public async Task A_job_only_scheduled_is_started_and_completed_in_the_same_unit()
    {
        var job = await DispatchedJobAsync("WO-7001");

        using var completed = await _world.SignedAsync(Route, "msg_1", Notice(job));

        completed.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher"))["status"]!.GetValue<string>().ShouldBe("completed");
    }

    [Fact]
    public async Task The_same_delivery_again_is_acknowledged_and_changes_nothing()
    {
        var job = await DispatchedJobAsync("WO-7001");
        using var first = await _world.SignedAsync(Route, "msg_1", Notice(job));
        var before = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");

        using var again = await _world.SignedAsync(Route, "msg_1", Notice(job));

        again.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher"))["updated_at"]!.GetValue<string>()
            .ShouldBe(before["updated_at"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_completion_resent_under_a_new_id_finds_the_job_completed_and_changes_nothing()
    {
        var job = await DispatchedJobAsync("WO-7001");
        using var first = await _world.SignedAsync(Route, "msg_1", Notice(job));
        var before = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");

        using var resent = await _world.SignedAsync(Route, "msg_2", Notice(job, minutes: 120));

        resent.StatusCode.ShouldBe(HttpStatusCode.OK);
        var after = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");
        (after["updated_at"]!.GetValue<string>(), after["on_site_minutes"]!.GetValue<long>())
            .ShouldBe((before["updated_at"]!.GetValue<string>(), 95L));
    }

    [Fact]
    public async Task A_delivery_signed_with_another_secret_is_refused_and_changes_nothing()
    {
        var job = await DispatchedJobAsync("WO-7001");
        var forged = "whsec_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("not-the-technician-app-secret-at-all"));

        using var refused = await _world.SignedAsync(Route, "msg_1", Notice(job), secret: forged);

        refused.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher"))["status"]!.GetValue<string>().ShouldBe("scheduled");
    }

    [Fact]
    public async Task A_notice_naming_another_tenant_finds_no_such_job_there()
    {
        var job = await DispatchedJobAsync("WO-7001");

        using var elsewhere = await _world.SignedAsync(Route, "msg_1", Notice(job, tenant: FieldServiceWorld.South.Value));

        elsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher"))["status"]!.GetValue<string>().ShouldBe("scheduled");
    }

    [Fact]
    public async Task A_job_nobody_scheduled_is_not_completed_from_the_app()
    {
        var job = await _world.NewJobAsync(_seed, "WO-7001");

        using var refused = await _world.SignedAsync(Route, "msg_1", Notice(job));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher"))["status"]!.GetValue<string>().ShouldBe("new");
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private async Task<Guid> DispatchedJobAsync(string reference)
    {
        var job = await _world.NewJobAsync(_seed, reference);
        await _world.DispatchAsync(job, _tenInBratislava);
        return job;
    }

    private async Task<JsonObject> VisitOfAsync(Guid job) =>
        (await _world.ReadAsync($"/api/visits?work_order_id=eq.{job}", "dispatcher"))["items"]!.AsArray().Single()!.AsObject();

    private static string Notice(Guid job, int minutes = 95, Guid? tenant = null) =>
        $"{{\"workOrderId\":\"{job}\",\"tenantId\":\"{tenant ?? FieldServiceWorld.North.Value}\",\"completedOn\":\"2026-11-16\",\"minutesOnSite\":{minutes}}}";
}
