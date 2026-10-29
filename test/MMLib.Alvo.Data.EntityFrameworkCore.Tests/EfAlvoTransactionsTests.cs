using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// <see cref="EfAlvoTransactions"/> over SQLite in memory (spec 2026-10-14 §4.5): what commits together, what a savepoint
/// undoes, what poisons, what is refused, and the unit's scope. The both-engine facts are <c>TransactionFacts</c>.
/// </summary>
/// <remarks>
/// Outside a unit this world's writes run its <c>StubBeforeHookRunner</c>; inside one they run whatever the unit's scope
/// resolves — <c>AddAlvo</c>'s real runner here — so a patch set on <c>world.Hooks</c> does not reach a write inside a unit
/// (Ruling 42). A batch the rules refuse is not tested for its rollback here: it writes nothing before it refuses
/// (Ruling 31), so `UnitSavepointFacts` holds that branch.
/// </remarks>
public sealed class EfAlvoTransactionsTests
{
    private static readonly string _outbox = OutboxTable.NameFor(new AlvoOptions().SchemaPrefix);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Two_writes_and_an_event_commit_together()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("first"), ct);
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("second"), ct);
            await unit.PublishAsync("notes.filed", "notes/1", null, ct);
        }, cancellationToken: Ct);

        (await NotesAsync(world)).ShouldBe(2L);
        (await world.ScalarAsync($"SELECT COUNT(*) FROM {_outbox}")).ShouldBe(3L);
    }

    [Fact]
    public async Task A_delegate_that_throws_leaves_no_row_and_no_event()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("first"), ct);
            await unit.PublishAsync("notes.filed", "notes/1", null, ct);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
        (await world.ScalarAsync($"SELECT COUNT(*) FROM {_outbox}")).ShouldBe(0L);
    }

    [Fact]
    public async Task A_read_inside_a_unit_sees_the_unit_s_own_writes()
    {
        await using var world = await StartAsync();

        var seen = await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            var created = await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("mine"), ct);
            var read = await unit.GetAsync(WritePathFixture.Entity, (Guid)created["id"]!, ct);
            var page = await unit.QueryAsync(new AlvoQuery { Entity = WritePathFixture.Entity }, ct);
            return (read?["title"], page.Items.Count);
        }, cancellationToken: Ct);

        seen.ShouldBe(("mine", 1));
    }

    [Fact]
    public async Task An_engine_failure_after_a_write_rolls_that_write_back_and_the_unit_goes_on()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("kept"), ct);
            world.Statements.FailWhenContains = "SELECT";
            world.Statements.FailuresLeft = 1;
            await Should.ThrowAsync<SqliteException>(() => unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("undone"), ct));
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), ct);
        }, cancellationToken: Ct);

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" WHERE "title" = 'undone'""")).ShouldBe(0L);
        (await NotesAsync(world)).ShouldBe(2L);
    }

    [Fact]
    public async Task A_batch_failing_at_the_engine_inside_a_unit_leaves_none_of_its_rows()
    {
        await using var world = await StartAsync();

        await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("kept"), ct);
            world.Statements.FailWhenContains = "SELECT";
            world.Statements.FailuresLeft = 1;
            await Should.ThrowAsync<SqliteException>(() => unit.CreateManyAsync(
                WritePathFixture.Entity, [WritePathFixture.CreatePayload("b1"), WritePathFixture.CreatePayload("b2")], ct));
        }, cancellationToken: Ct);

        (await world.ScalarAsync("""SELECT COUNT(*) FROM "note" WHERE "title" LIKE 'b%'""")).ShouldBe(0L);
        (await NotesAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task A_data_port_call_inside_a_unit_is_refused_at_once_and_rolls_the_unit_back()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("side"), world.Caller, cancellationToken: ct);
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_caught_refusal_still_rolls_the_unit_back()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            try
            {
                await world.Data.GetAsync(WritePathFixture.Entity, Guid.NewGuid(), world.Caller, ct);
            }
            catch (AlvoUnitInProgressException)
            {
            }
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_nested_TransactAsync_is_refused()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<AlvoUnitInProgressException>(() => world.Transactions.TransactAsync(world.Caller, (_, ct) =>
            world.Transactions.TransactAsync(world.Caller, (_, _) => Task.CompletedTask, cancellationToken: ct), cancellationToken: Ct));
    }

    [Fact]
    public async Task An_idempotency_key_inside_a_unit_is_an_argument_exception_and_a_caught_one_still_rolls_back()
    {
        await using var world = await StartAsync();
        ArgumentException? caught = null;

        var thrown = await Should.ThrowAsync<ArgumentException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("unit"), ct);
            caught = await Should.ThrowAsync<ArgumentException>(() => world.Data.CreateAsync(
                WritePathFixture.Entity, WritePathFixture.CreatePayload("keyed"), world.Caller, new AlvoIdempotency("key-1", "fp-1"), ct));
        }, cancellationToken: Ct));

        thrown.ShouldBeSameAs(caught);
        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_write_one_hop_deeper_past_the_limit_is_refused_and_a_caught_refusal_still_rolls_back()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { Depth = AlvoChainDepthLimit.DefaultMaxDepth } };

        await Should.ThrowAsync<AlvoChainDepthExceededException>(() => world.Transactions.TransactAsync(caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("at the limit"), ct);
            Should.Throw<AlvoChainDepthExceededException>(() => ((EfAlvoTransaction)unit).Deeper());
        }, cancellationToken: Ct));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task AsSystem_acts_as_the_system_in_the_caller_s_tenant_with_the_caller_s_provenance()
    {
        await using var world = await StartAsync();
        var caller = world.Caller with { Provenance = new AlvoProvenance { CorrelationId = "req-1", Depth = 2 } };

        var system = await world.Transactions.TransactAsync(caller, (unit, _) => Task.FromResult(unit.AsSystem().Context), cancellationToken: Ct);

        (system.ActorKind, system.Tenant, system.Provenance).ShouldBe((AlvoActorKind.System, caller.Tenant, caller.Provenance));
    }

    [Fact]
    public async Task Two_writes_in_one_unit_resolve_one_scope_distinct_from_the_request_s_and_the_next_unit_s()
    {
        var log = new HookRunnerLog();
        await using var world = await StartAsync(services: services =>
        {
            services.AddSingleton(log);
            services.AddScoped<IBeforeHookRunner, ScopeProbeHookRunner>();
        });
        await using var request = world.Scopes.CreateAsyncScope();
        var requestRunner = request.ServiceProvider.GetRequiredService<IBeforeHookRunner>();

        await TwoWritesAsync(world);
        var first = log.Runners.Distinct().ShouldHaveSingleItem();
        log.Runners.Count.ShouldBe(2);
        await TwoWritesAsync(world);

        first.ShouldNotBeSameAs(requestRunner);
        log.Runners.Distinct().Count().ShouldBe(2, "the next unit opens a scope of its own");
    }

    [Fact]
    public async Task A_transaction_used_after_its_unit_ended_is_refused()
    {
        await using var world = await StartAsync();
        IAlvoTransaction? kept = null;

        await world.Transactions.TransactAsync(world.Caller, (unit, _) =>
        {
            kept = unit;
            return Task.CompletedTask;
        }, cancellationToken: Ct);

        (await Should.ThrowAsync<InvalidOperationException>(() => kept!.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("late"), Ct)))
            .Message.ShouldContain("has ended");
        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_data_call_from_work_that_outlives_the_unit_is_not_refused()
    {
        await using var world = await StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<AlvoRecord>? outliving = null;

        await world.Transactions.TransactAsync(world.Caller, (_, _) =>
        {
            outliving = Task.Run(async () =>
            {
                await release.Task;
                return await world.Data.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("later"), world.Caller, cancellationToken: Ct);
            }, Ct);
            return Task.CompletedTask;
        }, cancellationToken: Ct);
        release.SetResult();

        (await outliving!)["title"].ShouldBe("later");
    }

    [Fact]
    public async Task A_cancelled_caller_rolls_the_unit_back_and_sees_its_cancellation()
    {
        await using var world = await StartAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("cancelled"), ct);
            await caller.CancelAsync();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, cancellationToken: caller.Token));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task Options_are_checked_before_a_unit_opens()
    {
        await using var world = await StartAsync();

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => world.Transactions.TransactAsync(
            world.Caller, (_, _) => Task.CompletedTask, new AlvoTransactionOptions { Timeout = TimeSpan.Zero }, Ct));

        await TwoWritesAsync(world);
        (await NotesAsync(world)).ShouldBe(2L);
    }

    [Fact]
    public async Task A_cancel_before_the_commit_rolls_back_even_when_the_delegate_ignores_its_token()
    {
        await using var world = await StartAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await Should.ThrowAsync<OperationCanceledException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("cancelled"), ct);
            await caller.CancelAsync();
        }, cancellationToken: caller.Token));

        (await NotesAsync(world)).ShouldBe(0L);
    }

    [Fact]
    public async Task A_cancel_during_the_commit_reports_success_with_the_data_committed()
    {
        await using var world = await StartAsync();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var transact = world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("committed"), ct);
            world.Commits.Hold();
        }, cancellationToken: caller.Token);
        await world.Commits.Reached;
        await caller.CancelAsync();
        world.Commits.Release();

        await transact;
        (await NotesAsync(world)).ShouldBe(1L);
    }

    [Fact]
    public async Task Every_verb_commits_with_the_unit()
    {
        await using var world = await StartAsync();
        var rows = await SeedFiveAsync(world);
        var fresh = Guid.NewGuid();

        await world.Transactions.TransactAsync(world.Caller, (unit, ct) => EveryVerbAsync(unit, rows, fresh, ct), cancellationToken: Ct);

        (await TitlesAsync(world)).ShouldBe("fresh,patched2,replaced2,updated2");
    }

    [Fact]
    public async Task Every_verb_rolls_back_with_the_unit()
    {
        await using var world = await StartAsync();
        var rows = await SeedFiveAsync(world);

        await Should.ThrowAsync<InvalidOperationException>(() => world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await EveryVerbAsync(unit, rows, Guid.NewGuid(), ct);
            throw new InvalidOperationException("the host changed its mind");
        }, cancellationToken: Ct));

        (await TitlesAsync(world)).ShouldBe("deleted,patched,removed,replaced,updated");
    }

    [Fact]
    public async Task A_caller_without_delete_rights_is_refused_inside_a_unit_as_outside_and_the_unit_goes_on()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor(delete: null));
        var id = await world.SeedAsync("kept");
        var outside = await RefusalsAsync(
            () => world.Data.DeleteAsync(WritePathFixture.Entity, id, world.Caller, cancellationToken: Ct),
            () => world.Data.DeleteManyAsync(WritePathFixture.Entity, [id], world.Caller, cancellationToken: Ct));

        var inside = await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            var refusals = await RefusalsAsync(
                () => unit.DeleteAsync(WritePathFixture.Entity, id, cancellationToken: ct),
                () => unit.DeleteManyAsync(WritePathFixture.Entity, [id], ct));
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), ct);
            return refusals;
        }, cancellationToken: Ct);

        inside.ShouldBe(outside);
        (await TitlesAsync(world)).ShouldBe("after,kept");
    }

    [Fact]
    public async Task A_caller_without_update_rights_is_refused_inside_a_unit_as_outside_and_the_unit_goes_on()
    {
        await using var world = await StartAsync(WritePathFixture.Descriptor(update: null));
        var id = await world.SeedAsync("kept");
        var outside = await RefusalsAsync(
            () => world.Data.UpdateAsync(WritePathFixture.Entity, id, Title("changed"), world.Caller, cancellationToken: Ct),
            () => world.Data.ReplaceAsync(WritePathFixture.Entity, id, WritePathFixture.Payload("changed"), world.Caller, cancellationToken: Ct),
            () => world.Data.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(id, Title("changed"))], world.Caller, cancellationToken: Ct));

        var inside = await world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            var refusals = await RefusalsAsync(
                () => unit.UpdateAsync(WritePathFixture.Entity, id, Title("changed"), cancellationToken: ct),
                () => unit.ReplaceAsync(WritePathFixture.Entity, id, WritePathFixture.Payload("changed"), cancellationToken: ct),
                () => unit.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(id, Title("changed"))], ct));
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("after"), ct);
            return refusals;
        }, cancellationToken: Ct);

        inside.ShouldBe(outside);
        (await TitlesAsync(world)).ShouldBe("after,kept");
    }

    private static Task<WritePathWorld> StartAsync(AlvoDescriptor? descriptor = null, Action<IServiceCollection>? services = null) =>
        WritePathWorld.StartAsync(descriptor ?? WritePathFixture.Descriptor(), WritePathFixture.Schema(), services);

    private static async Task<long> NotesAsync(WritePathWorld world) =>
        (long)(await world.ScalarAsync("""SELECT COUNT(*) FROM "note" """))!;

    private static async Task<string?> TitlesAsync(WritePathWorld world) =>
        (string?)await world.ScalarAsync("""SELECT group_concat("title", ',') FROM (SELECT "title" FROM "note" ORDER BY "title")""");

    private static async Task<Guid[]> SeedFiveAsync(WritePathWorld world) =>
    [
        await world.SeedAsync("updated"),
        await world.SeedAsync("replaced"),
        await world.SeedAsync("deleted"),
        await world.SeedAsync("patched"),
        await world.SeedAsync("removed"),
    ];

    private static async Task EveryVerbAsync(IAlvoTransaction unit, Guid[] rows, Guid fresh, CancellationToken ct)
    {
        await unit.UpdateAsync(WritePathFixture.Entity, rows[0], Title("updated2"), cancellationToken: ct);
        (await unit.ReplaceAsync(WritePathFixture.Entity, rows[1], WritePathFixture.Payload("replaced2"), cancellationToken: ct)).Created.ShouldBeFalse();
        (await unit.ReplaceAsync(WritePathFixture.Entity, fresh, WritePathFixture.Payload("fresh"), cancellationToken: ct)).Created.ShouldBeTrue();
        await unit.DeleteAsync(WritePathFixture.Entity, rows[2], cancellationToken: ct);
        (await unit.UpdateManyAsync(WritePathFixture.Entity, [new AlvoRowPatch(rows[3], Title("patched2"))], ct)).Succeeded.ShouldBeTrue();
        (await unit.DeleteManyAsync(WritePathFixture.Entity, [rows[4]], ct)).Succeeded.ShouldBeTrue();
    }

    private static async Task<string[]> RefusalsAsync(params Func<Task>[] calls)
    {
        var messages = new string[calls.Length];
        for (var index = 0; index < calls.Length; index++)
        {
            messages[index] = (await Should.ThrowAsync<AlvoAuthorizationException>(calls[index])).Message;
        }

        return messages;
    }

    private static Dictionary<string, object?> Title(string title) => new(StringComparer.Ordinal) { ["title"] = title };

    private static Task TwoWritesAsync(WritePathWorld world) =>
        world.Transactions.TransactAsync(world.Caller, async (unit, ct) =>
        {
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("one"), ct);
            await unit.CreateAsync(WritePathFixture.Entity, WritePathFixture.CreatePayload("two"), ct);
        }, cancellationToken: Ct);

    private sealed class HookRunnerLog
    {
        internal List<IBeforeHookRunner> Runners { get; } = [];
    }

    private sealed class ScopeProbeHookRunner(HookRunnerLog log) : IBeforeHookRunner
    {
        public IReadOnlyDictionary<string, object?> Run(
            string entity, DataOperation operation, AlvoRecord candidate, AlvoRecord? previous, AlvoContext context, DateTimeOffset now)
        {
            log.Runners.Add(this);
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }
    }
}
