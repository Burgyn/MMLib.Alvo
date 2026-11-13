using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Samples.FieldService.Tests.Dispatch;

public sealed class DispatchCommandTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _tenInBratislava = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _nineInTheEvening = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private HttpWorld _world = null!;
    private (Guid Region, Guid Customer) _seed;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await HttpWorld.StartAsync(Ct);
        _seed = await _world.SeedAsync();
    }

    [Fact]
    public async Task A_dispatcher_schedules_a_new_job_and_books_its_visit_in_one_step()
    {
        var job = await NewJobAsync("WO-7001");

        using var dispatched = await DispatchAsync(job, "dispatcher", _tenInBratislava);

        dispatched.StatusCode.ShouldBe(HttpStatusCode.OK);
        var answer = (await dispatched.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        answer["jobNumber"]!.GetValue<string>().ShouldBe("JOB-2026-00001");
        var stored = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");
        (stored["status"]!.GetValue<string>(), stored["assigned_to"]!.GetValue<Guid>()).ShouldBe(("scheduled", FieldServiceWorld.Technician));
        (await VisitsOfAsync(job)).Select(Id).ShouldBe([answer["visit"]!.GetValue<Guid>()]);
    }

    [Fact]
    public async Task A_retried_dispatch_replays_the_first_answer_and_books_nothing_twice()
    {
        var job = await NewJobAsync("WO-7001");

        using var first = await DispatchAsync(job, "dispatcher", _tenInBratislava, idempotency: "dispatch-7001");
        using var retried = await DispatchAsync(job, "dispatcher", _tenInBratislava, idempotency: "dispatch-7001");

        retried.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await retried.Content.ReadAsStringAsync(Ct)).ShouldBe(await first.Content.ReadAsStringAsync(Ct));
        (await VisitsOfAsync(job)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_technician_may_not_dispatch_and_nothing_is_written()
    {
        var job = await NewJobAsync("WO-7001", assignedTo: FieldServiceWorld.Technician);

        using var refused = await DispatchAsync(job, "technician", _tenInBratislava);

        refused.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        await ShouldStillBeNewAsync(job);
    }

    [Fact]
    public async Task A_job_already_scheduled_is_a_state_conflict_not_a_refusal_of_the_caller()
    {
        var job = await NewJobAsync("WO-7001");
        using var first = await DispatchAsync(job, "dispatcher", _tenInBratislava);

        using var again = await DispatchAsync(job, "dispatcher", _tenInBratislava.AddDays(1));

        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ProblemTypeAsync(again)).ShouldBe("https://alvo.dev/errors/conflict");
        (await VisitsOfAsync(job)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_dispatcher_of_another_tenant_finds_no_such_job()
    {
        var job = await NewJobAsync("WO-7001");

        using var elsewhere = await DispatchAsync(job, "dispatcher-south", _tenInBratislava);

        elsewhere.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        await ShouldStillBeNewAsync(job);
    }

    [Fact]
    public async Task A_visit_that_would_end_after_closing_is_refused_at_ends_at_and_the_job_stays_new()
    {
        var job = await NewJobAsync("WO-7001");
        var halfPastSix = new DateTimeOffset(2026, 11, 16, 17, 30, 0, TimeSpan.Zero);

        using var refused = await _world.SendAsync(
            $"/commands/work-orders/{job}/dispatch", "dispatcher",
            body: new { technicianId = FieldServiceWorld.Technician, startsAt = halfPastSix, durationMinutes = 120 });

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ViolationsAsync(refused)).ShouldBe([("outside-service-hours", "/ends_at")]);
        await ShouldStillBeNewAsync(job);
    }

    [Theory]
    [InlineData("{\"startsAt\":\"2026-11-16T09:00:00Z\",\"durationMinutes\":60}", "required", "/technicianId")]
    [InlineData("{\"technicianId\":\"0a51c4e1-0002-4000-8000-000000000002\",\"durationMinutes\":60}", "required", "/startsAt")]
    [InlineData("{\"technicianId\":\"0a51c4e1-0002-4000-8000-000000000002\",\"startsAt\":\"2026-11-16T09:00:00Z\"}", "required", "/durationMinutes")]
    [InlineData("{\"technicianId\":\"0a51c4e1-0002-4000-8000-000000000002\",\"startsAt\":\"2026-11-16T09:00:00Z\",\"durationMinutes\":0}", "out-of-range", "/durationMinutes")]
    [InlineData("{\"technicianId\":\"0a51c4e1-0002-4000-8000-000000000002\",\"startsAt\":\"2026-11-16T09:00:00Z\",\"durationMinutes\":2000000000}", "out-of-range", "/durationMinutes")]
    public async Task An_incomplete_or_absurd_request_is_422_at_the_field_and_writes_nothing(string body, string code, string field)
    {
        var job = await NewJobAsync("WO-7001");

        using var refused = await _world.SendRawAsync($"/commands/work-orders/{job}/dispatch", "dispatcher", body);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ViolationsAsync(refused)).ShouldBe([(code, field)]);
        await ShouldStillBeNewAsync(job);
    }

    [Fact]
    public async Task A_double_booked_technician_rolls_the_whole_unit_back()
    {
        using var booked = await DispatchAsync(await NewJobAsync("WO-7001"), "dispatcher", _tenInBratislava);
        var second = await NewJobAsync("WO-7002");

        using var refused = await DispatchAsync(second, "dispatcher", _tenInBratislava.AddMinutes(30));

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        await ShouldStillBeNewAsync(second);
    }

    [Fact]
    public async Task A_slot_outside_the_customers_hours_is_refused_and_the_job_stays_new()
    {
        var job = await NewJobAsync("WO-7001");

        using var refused = await DispatchAsync(job, "dispatcher", _nineInTheEvening);

        refused.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ViolationsAsync(refused)).ShouldBe([("outside-service-hours", "/scheduled_for")]);
        await ShouldStillBeNewAsync(job);
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private async Task<HttpResponseMessage> DispatchAsync(Guid job, string key, DateTimeOffset startsAt, string? idempotency = null) =>
        await _world.SendAsync(
            $"/commands/work-orders/{job}/dispatch",
            key,
            body: new { technicianId = FieldServiceWorld.Technician, startsAt, durationMinutes = 90 },
            idempotency: idempotency);

    private Task<Guid> NewJobAsync(string reference, Guid? assignedTo = null) =>
        _world.NewJobAsync(_seed, reference, assignedTo: assignedTo);

    private async Task<List<JsonObject>> VisitsOfAsync(Guid job)
    {
        var page = await _world.ReadAsync($"/api/visits?work_order_id=eq.{job}", "dispatcher");
        return [.. page["items"]!.AsArray().Select(row => row!.AsObject())];
    }

    private async Task ShouldStillBeNewAsync(Guid job)
    {
        var stored = await _world.ReadAsync($"/api/work_orders/{job}", "dispatcher");
        stored["status"]!.GetValue<string>().ShouldBe("new");
        stored["scheduled_for"].ShouldBeNull();
        (await VisitsOfAsync(job)).ShouldBeEmpty();
    }

    private static async Task<List<(string Code, string Pointer)>> ViolationsAsync(HttpResponseMessage refused)
    {
        var problem = (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        return [.. problem["violations"]!.AsArray().Select(v => (v!["code"]!.GetValue<string>(), v["pointer"]!.GetValue<string>()))];
    }

    private static async Task<string> ProblemTypeAsync(HttpResponseMessage refused) =>
        (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!["type"]!.GetValue<string>();

    private static Guid Id(JsonObject row) => row["id"]!.GetValue<Guid>();
}
