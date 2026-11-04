using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Data;
using MMLib.Alvo.Tests.Extensions;
using NSubstitute;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>
/// The core's write-hook runner (spec 2026-10-14 §4.4): what it selects and in what order, and what it turns a run into — a
/// patch, a rejection, or a failure that has already poisoned the unit on the flow.
/// </summary>
public sealed class WriteExtensionRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Hooks_run_by_order_then_entity_before_every_entity_then_registration()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo
            .Entity("*", every => every.BeforeWrite("every", Record(ran, "every")))
            .Entity("tickets", t => t
                .BeforeWrite("late", Record(ran, "late"), configure: o => o.Order = 5)
                .BeforeWrite("first", Record(ran, "first"))
                .BeforeWrite("second", Record(ran, "second"))));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct);

        ran.ShouldBe(["first", "second", "every", "late"]);
    }

    [Fact]
    public async Task A_hook_whose_condition_does_not_hold_does_not_run()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("resolved-only", Record(ran, "ran"), when: "new.status == 'resolved'")));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket("open")), Ct);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_later_hook_sees_an_earlier_hook_s_set_and_the_patch_is_their_union()
    {
        string? seen = null;
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("titles", (BeforeWrite<AlvoRecord> write) => { write.Set("title", "set"); return ValueTask.CompletedTask; })
            .BeforeWrite("codes", (BeforeWrite<AlvoRecord> write) => { seen = (string?)write.New["title"]; write.Set("code", "C-1"); return ValueTask.CompletedTask; })));

        var patch = await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct);

        seen.ShouldBe("set");
        patch.ShouldBe(new Dictionary<string, object?> { ["title"] = "set", ["code"] = "C-1" });
    }

    [Fact]
    public async Task A_later_hook_s_condition_sees_an_earlier_hook_s_set_both_ways()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("resolve", (BeforeWrite<AlvoRecord> write) => { write.Set("status", "resolved"); return ValueTask.CompletedTask; })
            .BeforeWrite("guard-resolved", Record(ran, "guard-resolved"), when: "new.status == 'resolved'")
            .BeforeWrite("guard-open", Record(ran, "guard-open"), when: "new.status == 'open'")));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket("open")), Ct);

        ran.ToArray().ShouldBe(["guard-resolved"], customMessage: "a guard an earlier Set made applicable runs, and one it made inapplicable does not");
    }

    [Fact]
    public async Task A_rejection_stops_the_stage_discards_its_sets_and_is_logged_with_ids_and_the_code_only()
    {
        var ran = new ConcurrentQueue<string>();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("guard", (BeforeWrite<AlvoRecord> write) =>
            {
                write.Set("title", "discarded");
                write.Conflict("taken", "This title is taken by someone's secret project.", "title");
                return ValueTask.CompletedTask;
            })
            .BeforeWrite("after-guard", Record(ran, "after-guard"))));

        var rejection = await Should.ThrowAsync<AlvoWriteRejectedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        (rejection.Kind, rejection.Code, rejection.Pointer, rejection.ExtensionId).ShouldBe((AlvoWriteRejectionKind.Conflict, "taken", "/title", "guard"));
        ran.ShouldBeEmpty();
        var logged = world.Rejections.Recorded.ShouldHaveSingleItem();
        (logged.Extension, logged.Entity, logged.Operation, logged.Kind, logged.Code, logged.Actor)
            .ShouldBe(("guard", "tickets", "create", "conflict", "taken", AlvoIdempotency.IdentityOf(RunnerWorld.Caller)));
    }

    [Fact]
    public async Task A_host_exception_is_a_hook_failure_that_poisons_the_unit_and_never_carries_the_host_s_message()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("boom", Throws<BeforeWrite<AlvoRecord>>(new InvalidOperationException("SECRET connection string")))));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        (failure.Extension, failure.Reason).ShouldBe(("boom", HookFailedException.Exception));
        failure.Message.ShouldNotContain("SECRET");
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_caught_budget_refusal_still_fails_the_hook_and_poisons_the_unit()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("greedy", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                for (var call = 0; call < 51; call++)
                {
                    await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
                }
            }
            catch (AlvoBudgetExceededException)
            {
            }
        })));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.Budget);
        unit.Poisoned.ShouldContain(failure);
    }

    [Fact]
    public async Task A_hook_that_overruns_its_timeout_fails_with_timeout_even_when_it_catches_the_cancellation()
    {
        var clock = new FakeTimeProvider();
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("slow", async (BeforeWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            try
            {
                await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
            }
            catch (OperationCanceledException)
            {
            }
        })), clock);
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        failure.Reason.ShouldBe(HookFailedException.Timeout);
        unit.Blamed.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_nested_write_to_the_row_in_its_own_hook_stage_is_a_self_write_even_when_caught()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("echo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            try
            {
                await write.Transaction.UpdateAsync("tickets", (Guid)write.Saved["id"]!, new Dictionary<string, object?> { ["title"] = "again" }, cancellationToken: ct);
            }
            catch (HookFailedException)
            {
            }
        })));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.SelfWrite);
        unit.Poisoned.ShouldContain(failure);
    }

    [Fact]
    public async Task AsSystem_is_the_system_in_the_caller_s_tenant_with_the_caller_s_provenance_and_it_is_logged()
    {
        AlvoContext? seen = null;
        var logs = new CapturingLogger();
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.BeforeWrite("reader", (BeforeWrite<AlvoRecord> write) => { seen = write.Transaction.AsSystem().Context; return ValueTask.CompletedTask; })),
            services: services => services.AddLogging(logging => logging.AddProvider(logs)));
        var caller = RunnerWorld.Caller with { ActorKind = AlvoActorKind.User, Provenance = new AlvoProvenance { Depth = 3, CorrelationId = "c-1" } };

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: new FakeHookTransaction(caller)) with { Caller = caller }, Ct);

        (seen!.ActorKind, seen.Tenant, seen.Provenance).ShouldBe((AlvoActorKind.System, caller.Tenant, caller.Provenance));
        var line = logs.Entries.Select(entry => entry.Message).Single(message => message.Contains("acted as the system", StringComparison.Ordinal));
        line.ShouldContain(AlvoIdempotency.IdentityOf(caller));
        line.ShouldContain("correlation c-1");
    }

    [Fact]
    public async Task A_registered_hook_that_does_not_bind_the_current_catalog_fails_the_write_closed()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("typo", (BeforeWrite<AlvoRecord> _) => ValueTask.CompletedTask, when: "new.stauts == 'x'")));

        world.Runner.RunsInTransaction("tickets", AlvoWriteOperation.Create).ShouldBeTrue("registrations, not bindings, decide that a unit is needed");
        (await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct))).Reason
            .ShouldBe(HookFailedException.Unbound);
    }

    [Fact]
    public async Task A_switched_off_validate_hook_is_skipped()
    {
        var ran = new ConcurrentQueue<string>();
        var store = Substitute.For<IDeliveryStore>();
        store.DisabledAsync(Arg.Any<CancellationToken>()).Returns(["check"]);
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.Validate("check", (ValidateWrite<AlvoRecord> _) => { ran.Enqueue("check"); return ValueTask.CompletedTask; })),
            services: services => services.AddSingleton(store));

        await world.Runner.ValidateAsync(world.Before(RunnerWorld.Ticket()) with { Transaction = null, Services = null }, Ct);

        ran.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_depth_refusal_escaping_a_hook_passes_unchanged_and_a_nested_rejection_is_the_hook_s_failure()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t
            .BeforeWrite("deep", Throws<BeforeWrite<AlvoRecord>>(new AlvoChainDepthExceededException("too deep")))
            .AfterWrite("nested", Throws<AfterWrite<AlvoRecord>>(new AlvoWriteRejectedException("from a nested write")))));

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));
        (await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct)))
            .ShouldSatisfyAllConditions(failure => failure.Extension.ShouldBe("nested"), failure => failure.Reason.ShouldBe(HookFailedException.Exception));
    }

    [Fact]
    public async Task Validate_hands_the_hook_a_session_one_hop_deeper()
    {
        AlvoContext? asked = null;
        var sessions = Substitute.For<IAlvoSessions>();
        sessions.For(Arg.Do<AlvoContext>(context => asked = context)).Returns(Substitute.For<IAlvoSession>());
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.Validate("check", (ValidateWrite<AlvoRecord> write) => { _ = write.Data; return ValueTask.CompletedTask; })),
            services: services => services.AddSingleton(sessions));

        await world.Runner.ValidateAsync(world.Before(RunnerWorld.Ticket()) with { Transaction = null, Services = null }, Ct);

        asked!.Provenance.Depth.ShouldBe(1);
    }

    [Fact]
    public async Task A_rejecting_hook_s_sets_are_discarded_unchecked_so_the_caller_hears_the_rejection()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("guard", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("nope", "never written");
            write.Conflict("taken", "This title is taken.");
            return ValueTask.CompletedTask;
        })));

        (await Should.ThrowAsync<AlvoWriteRejectedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct))).Code.ShouldBe("taken");
    }

    [Fact]
    public async Task A_set_of_an_undeclared_field_fails_the_hook_with_invalid_set()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.BeforeWrite("setter", (BeforeWrite<AlvoRecord> write) =>
        {
            write.Set("nope", "x");
            return ValueTask.CompletedTask;
        })));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        failure.Reason.ShouldBe(HookFailedException.InvalidSet);
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_service_that_cannot_be_built_for_a_delegate_hook_is_the_hook_s_failure_not_an_escape()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(
            alvo => alvo.Entity("tickets", t => t.BeforeWrite("needs-clock", (BeforeWrite<AlvoRecord> _, IBrokenService broken) => ValueTask.CompletedTask)),
            services: services => services.AddSingleton<IBrokenService>(_ => throw new InvalidOperationException("SECRET factory")));
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct));

        (failure.Extension, failure.Reason).ShouldBe(("needs-clock", HookFailedException.Exception));
        unit.Poisoned.ShouldHaveSingleItem().ShouldBeSameAs(failure);
    }

    [Fact]
    public async Task A_typed_hook_reads_a_required_rollup_field_a_create_has_not_filled_yet_as_its_default()
    {
        long? seen = null;
        await using var world = RunnerWorld.Start(alvo => alvo.Entity<ScoredTicket>(t => t.BeforeWrite("scores", (BeforeWrite<ScoredTicket> write) =>
        {
            seen = write.New.Score;
            return ValueTask.CompletedTask;
        })));
        var tickets = HandlerCatalog.Schema.Entities.Single(entity => entity.Name == "tickets");
        var score = new MMLib.Alvo.Schema.FieldSchema
        {
            Name = "score",
            Type = MMLib.Alvo.Schema.FieldType.Integer,
            Rollup = new MMLib.Alvo.Schema.RollupSchema { From = "notes", Via = "ticket_id", Op = MMLib.Alvo.Schema.RollupOperation.Count },
        };
        world.Services.GetRequiredService<IPolicyCatalogProvider>().SetCurrent(
            "handlers", HandlerCatalog.CatalogOf(new MMLib.Alvo.Schema.SchemaModel([tickets with { Fields = [.. tickets.Fields, score] }])));

        await world.Runner.BeforeWriteAsync(world.Before(RunnerWorld.Ticket()), Ct);

        seen.ShouldBe(0L);
    }

    private static Func<TContext, ValueTask> Throws<TContext>(Exception exception) => _ => throw exception;

    private static Func<BeforeWrite<AlvoRecord>, ValueTask> Record(ConcurrentQueue<string> ran, string name) => _ =>
    {
        ran.Enqueue(name);
        return ValueTask.CompletedTask;
    };
}

/// <summary>A service whose registration throws when it is built.</summary>
public interface IBrokenService;

[AlvoEntity("tickets")]
public sealed class ScoredTicket
{
    public Guid Id { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public long Score { get; init; }
}
