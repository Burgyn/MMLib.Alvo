using MMLib.Alvo.Events;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// The operations port's changes (spec 2026-10-14 HX-06): each one that changed something writes one log row and one
/// <c>ManagementAudit</c> line (acceptance (7)); one that changed nothing writes neither; switching a handler off is 409 (9).
/// </summary>
public sealed class AlvoOperationsWritesTests
{
    private const string Project = OperationsTestWorld.Project;
    private const string Handler = OperationsTestWorld.Handler;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Redelivering_a_dead_delivery_puts_it_back_and_audits_once()
    {
        using var world = new OperationsTestWorld();
        var dead = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");

        var redelivered = await world.Service.RedeliverAsync(Project, dead.Id, "fixed the template", Ct);

        redelivered.Status.ShouldBe("pending");
        redelivered.Attempts.ShouldBe(0);
        world.Store.Operations.ShouldHaveSingleItem().Operation.ShouldBe("redeliver");
        world.Audit.Lines.ShouldHaveSingleItem().ShouldBe(
            $"Management audit: ops-key:{ManagementCallers.Bootstrap.Value} made 'redeliver' on '{dead.Id}' (1 changed). Reason: fixed the template");
    }

    [Fact]
    public async Task Redelivering_a_pending_delivery_answers_it_unchanged_and_audits_nothing()
    {
        using var world = new OperationsTestWorld();
        var pending = await world.DeliveryAsync("tickets:a");

        (await world.Service.RedeliverAsync(Project, pending.Id, reason: null, Ct)).Status.ShouldBe("pending");

        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redelivering_a_running_delivery_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        var delivery = await world.DeliveryAsync("tickets:a");
        await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, delivery.Id, reason: null, Ct)))
            .Message.ShouldContain("running");
    }

    [Fact]
    public async Task Redelivering_for_a_handler_this_host_does_not_register_is_refused()
    {
        using var world = new OperationsTestWorld();
        var orphan = await world.DeliveryAsync("tickets:a", deadReason: "when-failed", subscriber: "renamed-away");

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, orphan.Id, reason: null, Ct)))
            .Message.ShouldContain("AddRetiredExtension");
        world.Store.Rows.ShouldHaveSingleItem().Status.ShouldBe("dead", "nothing was put back that the next start would refuse");
    }

    [Fact]
    public async Task Pausing_twice_audits_once_and_answers_paused()
    {
        using var world = new OperationsTestWorld();

        (await world.Service.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct)).Paused.ShouldBeTrue();
        (await world.Service.SetPausedAsync(Project, Handler, paused: true, "mail provider down", Ct)).Paused.ShouldBeTrue();
        (await world.Service.SetPausedAsync(Project, Handler, paused: false, reason: null, Ct)).Paused.ShouldBeFalse();

        world.Store.Operations.Select(row => row.Operation).ShouldBe(["pause", "resume"]);
        world.Audit.Lines.Count.ShouldBe(2);
        world.Audit.Lines[0].ShouldContain($"made 'pause' on '{Handler}'");
        world.Audit.Lines[1].ShouldContain($"made 'resume' on '{Handler}'");
    }

    [Fact]
    public async Task Pausing_an_unknown_handler_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.SetPausedAsync(Project, "nobody", paused: true, reason: null, Ct));
    }

    [Fact]
    public async Task Switching_a_handler_off_is_a_conflict_that_names_pausing()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.SetEnabledAsync(Project, Handler, enabled: false, reason: null, Ct)))
            .Message.ShouldContain($"subscribers/{Handler}/paused");
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Running_a_schedule_this_host_lacks_is_not_found()
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementExtensionNotFoundException>(() => world.Service.RunScheduleAsync(Project, "nightly-report", Ct));
    }

    [Theory]
    [InlineData("two\nlines")]
    [InlineData("tab\there")]
    [InlineData("line\u2028separator")]
    [InlineData("paragraph\u2029separator")]
    [InlineData("right-to-left\u202Eoverride")]
    [InlineData("zero\u200Bwidth")]
    public async Task A_reason_with_a_control_character_is_refused_before_anything_is_written(string reason)
    {
        using var world = new OperationsTestWorld();

        await Should.ThrowAsync<ManagementRequestException>(() => world.Service.SetPausedAsync(Project, Handler, paused: true, reason, Ct));

        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_reason_over_500_characters_is_refused()
    {
        using var world = new OperationsTestWorld();

        (await Should.ThrowAsync<ManagementRequestException>(() => world.Service.SetPausedAsync(Project, Handler, paused: true, new string('x', 501), Ct)))
            .Message.ShouldContain("500");
    }

    [Fact]
    public async Task Redelivering_a_loop_break_is_refused()
    {
        using var world = new OperationsTestWorld();
        var loop = await world.DeliveryAsync("tickets:a", deadReason: "chain-depth-exceeded");

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, loop.Id, reason: null, Ct)))
            .Message.ShouldContain("loop");
        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task Redelivering_while_the_record_runs_another_delivery_is_refused()
    {
        using var world = new OperationsTestWorld();
        var dead = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.DeliveryAsync("tickets:a");
        await world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [], Unordered = [Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, dead.Id, reason: null, Ct)))
            .Message.ShouldContain("running");
        world.Store.Operations.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_redeliver_another_operator_landed_first_answers_the_delivery_and_audits_nothing()
    {
        using var world = new OperationsTestWorld();
        var dead = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        world.Store.BeforeRedeliver = () => world.Store.RedeliverAsync(dead.Id, Concurrent(world), Ct);

        (await world.Service.RedeliverAsync(Project, dead.Id, reason: null, Ct)).Status.ShouldBe("pending");

        world.Store.Operations.ShouldHaveSingleItem().Actor.ShouldBe("other-operator");
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_lane_that_starts_running_between_the_check_and_the_reset_is_a_conflict()
    {
        using var world = new OperationsTestWorld();
        var dead = await world.DeliveryAsync("tickets:a", deadReason: "when-failed");
        await world.DeliveryAsync("tickets:a");
        world.Store.BeforeRedeliver = () => world.Store.ClaimAsync(new DeliveryClaim { Claimant = "w", Ordered = [], Unordered = [Handler], BatchSize = 1, Lease = TimeSpan.FromMinutes(1), OutboxMaxAttempts = 10, OutboxClaimLease = TimeSpan.FromMinutes(5) }, Ct);

        (await Should.ThrowAsync<ManagementConflictException>(() => world.Service.RedeliverAsync(Project, dead.Id, reason: null, Ct)))
            .Message.ShouldContain("running");
        world.Store.Operations.ShouldBeEmpty();
        world.Audit.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Every_write_refuses_an_unpublished_caller()
    {
        using var world = new OperationsTestWorld(admitted: false);

        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.RedeliverAsync(Project, Guid.NewGuid(), null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.SetPausedAsync(Project, Handler, true, null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.SetEnabledAsync(Project, Handler, false, null, Ct));
        await Should.ThrowAsync<ManagementForbiddenException>(() => world.Service.RunScheduleAsync(Project, "nightly-report", Ct));
    }

    private static DeliveryOperationEntry Concurrent(OperationsTestWorld world) => new()
    {
        Id = AlvoEventId.Create(world.Clock.GetUtcNow()),
        At = world.Clock.GetUtcNow(),
        Actor = "other-operator",
        Operation = "redeliver",
        Target = "concurrent",
    };
}
