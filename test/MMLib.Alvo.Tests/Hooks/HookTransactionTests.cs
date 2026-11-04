using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Tests.Hooks;

/// <summary>The hook's view of the unit (Rulings 10, 35): every call counted and run under the hook's token; writes marked.</summary>
public sealed class HookTransactionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_call_runs_under_the_hook_s_token_and_counts_reads_and_the_system_view_too()
    {
        await using var world = RunnerWorld.Start(alvo => alvo.Entity("tickets", t => t.AfterWrite("audit", async (AfterWrite<AlvoRecord> write, CancellationToken ct) =>
        {
            await write.Transaction.GetAsync("tickets", Guid.NewGuid(), CancellationToken.None);
            await write.Transaction.AsSystem().QueryAsync(new AlvoQuery { Entity = "notes" }, ct);
            await write.Transaction.CreateAsync("notes", new Dictionary<string, object?> { ["text"] = "audited" }, ct);
        }, configure: o => o.MaxOperations = 3)));
        var inner = new FakeHookTransaction(RunnerWorld.Caller);

        var writer = await world.Runner.AfterWriteAsync(world.Before(RunnerWorld.Ticket(), transaction: inner) with { Saved = RunnerWorld.Ticket() }, Ct);

        writer.ShouldBe("audit");
        inner.Calls.Count.ShouldBe(2, "the system view's call went to its own fake");
        inner.LastToken.CanBeCanceled.ShouldBeTrue("the hook's token, not the CancellationToken.None the hook passed");
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
