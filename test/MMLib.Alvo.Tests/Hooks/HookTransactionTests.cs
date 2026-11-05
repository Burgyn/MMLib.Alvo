using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tests.Data;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>The hook's view of the unit (Rulings 10, 35): every call counted and run under the hook's token; writes marked.</summary>
public sealed class HookTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_call_runs_under_the_hook_s_token_through_the_system_view_too()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("audit", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), CancellationToken.None);
            await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "notes" }, CancellationToken.None);
            await write.Transaction.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "audited" }, ct);
        })));
        var inner = new FakeHookTransaction(RunnerWorld.Caller);

        var writer = await world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: inner) with { Saved = RunnerWorld.Ticket() }, Ct);

        writer.ShouldBe("audit");
        (inner.Calls.Count, inner.System!.Calls.Count).ShouldBe((2, 1));
        inner.LastToken.CanBeCanceled.ShouldBeTrue("the hook's token, not the CancellationToken.None the hook passed");
        inner.System.LastToken.CanBeCanceled.ShouldBeTrue("the system view runs under the hook's token as well");
    }

    [Fact]
    public async Task A_call_through_the_system_view_counts_against_the_hook_s_budget()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("audit", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
            await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "notes" }, ct);
            await write.Transaction.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "audited" }, ct);
        }, configure: o => o.MaxOperations = 2)));

        var failure = await Should.ThrowAsync<HookFailedException>(() => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.Budget);
    }

    [Fact]
    public async Task A_write_through_the_system_view_to_the_held_row_is_a_self_write()
    {
        var unit = new FakeFlowUnit();
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("echo", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.AsSystem().UpdateAsync("tickets", RunnerWorld.TicketId, new Dictionary<string, object?> { ["title"] = "again" }, cancellationToken: ct);
        })));
        var inner = new FakeHookTransaction(RunnerWorld.Caller);
        using var flow = AlvoFlow.EnterUnit(unit, new AlvoProvenance());

        var failure = await Should.ThrowAsync<HookFailedException>(
            () => world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: inner) with { Saved = RunnerWorld.Ticket() }, Ct));

        failure.Reason.ShouldBe(HookFailedException.SelfWrite);
        unit.Poisoned.ShouldContain(failure);
        inner.System!.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_hook_that_only_reads_and_publishes_wrote_nothing()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("reader", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), ct);
            await write.PublishAsync("tickets.seen", "tickets/1", null, ct);
        })));

        (await world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket()) with { Saved = RunnerWorld.Ticket() }, Ct)).ShouldBeNull();
    }
}
