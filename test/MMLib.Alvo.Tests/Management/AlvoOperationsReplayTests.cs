using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// A replay (spec 2026-10-14 HX-06): a dry run counts exactly and changes nothing (acceptance (1)); a real run resets what it
/// selects and audits once (7); its idempotency key answers a retry with the first replay's count.
/// </summary>
public sealed class AlvoOperationsReplayTests
{
    private const string Project = OperationsTestWorld.Project;
    private const string Handler = OperationsTestWorld.Handler;

    private static readonly DateTimeOffset _preEpoch = new(1969, 12, 31, 0, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_replay_dry_run_counts_exactly_and_changes_nothing()
    {
        using var world = new OperationsTestWorld();
        foreach (var key in new[] { "tickets:a", "tickets:b", "tickets:c" })
        {
            await world.DeliveryAsync(key, deadReason: "when-failed");
        }

        var plan = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true }, Ct);

        plan.DryRun.ShouldBeTrue();
        plan.Count.ShouldBe(3);
        plan.Ids.Count.ShouldBe(3);
        world.Store.Rows.ShouldAllBe(row => row.Status == "dead");
        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_from_before_the_epoch_selects_from_the_first_event()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var plan = await world.Service.ReplayAsync(
            Project, new ManagementReplayRequest { Subscriber = Handler, From = DateTimeOffset.MinValue, DryRun = true }, Ct);

        plan.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_replay_up_to_before_the_epoch_selects_nothing()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var plan = await world.Service.ReplayAsync(
            Project, new ManagementReplayRequest { Subscriber = Handler, To = _preEpoch, DryRun = true }, Ct);

        plan.Count.ShouldBe(0);
    }

    [Fact]
    public async Task A_delivery_list_window_before_the_epoch_is_clamped_not_refused()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var fromBefore = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { From = DateTimeOffset.MinValue, Limit = 10 }, Ct);
        var toBefore = await world.Service.ListDeliveriesAsync(Project, new ManagementDeliveryQuery { From = DateTimeOffset.MinValue, To = _preEpoch, Limit = 10 }, Ct);

        fromBefore.Items.ShouldHaveSingleItem();
        toBefore.Items.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_resets_what_it_selects_and_audits_once()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.DeliveryAsync("tickets:b", deadReason: "when-failed");

        var plan = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "endpoint is back" }, Ct);

        plan.Count.ShouldBe(2);
        plan.Replayed.ShouldBeFalse();
        world.Store.Rows.ShouldAllBe(row => row.Status == "pending");
        world.Store.Operations.ShouldHaveSingleItem().Affected.ShouldBe(2);
        world.Audit.Lines.ShouldHaveSingleItem().ShouldContain("made 'replay' on 'send-confirmation' (2 changed)");
    }

    [Fact]
    public async Task A_replay_that_resets_nothing_still_records_and_audits_its_run()
    {
        using var world = new OperationsTestWorld();

        var plan = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler }, Ct);

        plan.Count.ShouldBe(0);
        world.Store.Operations.ShouldHaveSingleItem().Affected.ShouldBe(0);
        world.Audit.Lines.ShouldHaveSingleItem().ShouldContain("(0 changed)");
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("running")]
    [InlineData("retrying")]
    public async Task A_replay_of_an_unfinished_status_is_refused(string status)
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Status = status }, Ct)))
            .Message.ShouldContain("succeeded, dead, cancelled");
    }

    [Fact]
    public async Task A_replay_names_a_handler_this_host_registers()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = "send-confirmaton" }, Ct)))
            .Message.ShouldContain(Handler);
    }

    [Fact]
    public async Task A_replay_reason_on_two_lines_is_refused_before_anything_changes()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "back\nup" }, Ct));

        world.Store.Rows.ShouldAllBe(row => row.Status == "dead");
        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_key_on_a_dry_run_is_refused()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true, IdempotencyKey = "k-1" }, Ct)))
            .Message.ShouldContain("dry run");
    }

    [Fact]
    public async Task A_blank_key_is_refused()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementRequestException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = " " }, Ct));

        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retried_replay_answers_the_first_and_resets_nothing()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        var request = new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-7" };
        var first = await world.Service.ReplayAsync(Project, request, Ct);
        await world.DeliveryAsync("tickets:b", deadReason: "when-failed");

        var retried = await world.Service.ReplayAsync(Project, request, Ct);

        (retried.Count, retried.Replayed, retried.DryRun).ShouldBe((first.Count, true, false));
        retried.Ids.ShouldBe(first.Ids);
        world.Store.Rows.Count(row => row.Status == "dead").ShouldBe(1, "the retry reset nothing");
        world.Audit.Lines.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_retry_with_a_reworded_reason_is_the_same_replay()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "back", IdempotencyKey = "replay-9" }, Ct);

        var retried = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Reason = "endpoint is back", IdempotencyKey = "replay-9" }, Ct);

        (retried.Count, retried.Replayed).ShouldBe((1, true));
    }

    [Fact]
    public async Task A_key_spent_on_a_different_replay_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-8" }, Ct);

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() =>
            world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Status = "succeeded", IdempotencyKey = "replay-8" }, Ct));
    }

    [Fact]
    public async Task A_replay_leaves_out_loop_breaks()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "chain-depth-exceeded");
        await world.DeliveryAsync("tickets:b", deadReason: "when-failed");

        (await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, DryRun = true }, Ct)).Count.ShouldBe(1);
        (await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler }, Ct)).Count.ShouldBe(1);
        world.Store.Rows.ShouldContain(row => row.LastErrorType == "chain-depth-exceeded" && row.Status == "dead");
    }

    [Fact]
    public async Task A_replay_of_a_record_whose_delivery_is_running_is_refused()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.DeliveryAsync("tickets:a");
        await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [], Unordered = [Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler }, Ct)))
            .Message.ShouldContain("running");
        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler }, Ct));
    }

    [Fact]
    public void The_operations_port_resolves_to_the_one_service()
    {
        using var world = new OperationsTestWorld();

        world.Services.GetRequiredService<IAlvoOperations>().ShouldBeSameAs(world.Service);
    }

    [Fact]
    public async Task A_replay_whose_key_a_concurrent_run_recorded_first_answers_that_run()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        var service = ServiceOver(world, new RacedReplayStore(world.Store));

        var plan = await service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-10" }, Ct);

        (plan.Count, plan.Replayed, plan.DryRun).ShouldBe((1, true, false));
        plan.Ids.ShouldBe(world.Store.Operations.ShouldHaveSingleItem().Sample);
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_replay_whose_key_a_concurrent_different_run_recorded_first_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        var service = ServiceOver(world, new RacedReplayStore(world.Store, racingFingerprint: "another-request"));

        await Should.ThrowAsync<AlvoIdempotencyConflictException>(() =>
            service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-11" }, Ct));

        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_retry_spelling_the_same_window_in_another_offset_is_the_same_replay()
    {
        using var world = new OperationsTestWorld();
        var from = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, From = from, IdempotencyKey = "replay-12" }, Ct);

        var retried = await world.Service.ReplayAsync(
            Project, new ManagementReplayRequest { Subscriber = Handler, From = from.ToOffset(TimeSpan.FromHours(2)), IdempotencyKey = "replay-12" }, Ct);

        retried.Replayed.ShouldBeTrue();
    }

    [Fact]
    public async Task A_missing_status_is_the_dead_replay_it_defaults_to()
    {
        using var world = new OperationsTestWorld();
        await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, IdempotencyKey = "replay-13" }, Ct);

        var retried = await world.Service.ReplayAsync(Project, new ManagementReplayRequest { Subscriber = Handler, Status = null!, IdempotencyKey = "replay-13" }, Ct);

        (retried.Count, retried.Replayed).ShouldBe((1, true));
    }

    private static AlvoOperationsService ServiceOver(OperationsTestWorld world, IDeliveryStore store) => new(
        world.Services.GetRequiredService<ManagementGate>(),
        world.Services.GetRequiredService<ExtensionCatalog>(),
        world.Services.GetRequiredService<ExtensionStats>(),
        world.Services.GetRequiredService<ISchemaRegistry>(),
        world.Services.GetRequiredService<TimeProvider>(),
        world.Services.GetRequiredService<ILogger<AlvoOperationsService>>(),
        store);
}
