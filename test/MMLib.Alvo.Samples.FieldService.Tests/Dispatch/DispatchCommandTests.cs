using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.Samples.FieldService.Tests.Dispatch;

public sealed class DispatchCommandTests : IAsyncLifetime
{
    private static readonly DateTimeOffset _tenInBratislava = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _nineInTheEvening = new(2026, 11, 16, 20, 0, 0, TimeSpan.Zero);

    private HttpWorld _world = null!;
    private Guid _customer;
    private Guid _region;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _world = await HttpWorld.StartAsync(Ct);
        _region = Id(await _world.CreateAsync("regions", "admin", new JsonObject { ["code"] = "NORTH", ["name"] = "North" }));
        _customer = Id(await _world.CreateAsync("customers", "dispatcher", new JsonObject
        {
            ["tenant_id"] = FieldServiceWorld.North.Value,
            ["name"] = "Kaviareň Dunaj",
            ["tier"] = "standard",
            ["service_opens_hour"] = 7,
            ["service_closes_hour"] = 19,
        }));
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
    public async Task A_job_already_scheduled_is_not_dispatched_again()
    {
        var job = await NewJobAsync("WO-7001");
        using var first = await DispatchAsync(job, "dispatcher", _tenInBratislava);

        using var again = await DispatchAsync(job, "dispatcher", _tenInBratislava.AddDays(1));

        again.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await VisitsOfAsync(job)).Count.ShouldBe(1);
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
        var problem = (await refused.Content.ReadFromJsonAsync<JsonObject>(Ct))!;
        problem["violations"]!.AsArray().Select(v => (v!["code"]!.GetValue<string>(), v["pointer"]!.GetValue<string>()))
            .ShouldBe([("outside-service-hours", "/scheduled_for")]);
        await ShouldStillBeNewAsync(job);
    }

    public async ValueTask DisposeAsync() => await _world.DisposeAsync();

    private async Task<HttpResponseMessage> DispatchAsync(Guid job, string key, DateTimeOffset startsAt, string? idempotency = null) =>
        await _world.SendAsync(
            $"/commands/work-orders/{job}/dispatch",
            key,
            body: new { technicianId = FieldServiceWorld.Technician, startsAt, durationMinutes = 90 },
            idempotency: idempotency);

    private async Task<Guid> NewJobAsync(string reference, Guid? assignedTo = null) =>
        Id(await _world.CreateAsync("work_orders", "dispatcher", new JsonObject
        {
            ["tenant_id"] = FieldServiceWorld.North.Value,
            ["reference"] = reference,
            ["title"] = $"Job {reference}",
            ["status"] = "new",
            ["priority"] = 3,
            ["access_code"] = "4711",
            ["customer_id"] = _customer,
            ["region_id"] = _region,
            ["assigned_to"] = assignedTo,
        }));

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

    private static Guid Id(JsonObject row) => row["id"]!.GetValue<Guid>();
}
