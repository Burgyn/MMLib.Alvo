namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The two framework tables the write path creates for itself — the outbox and the idempotency record — are
/// ensured <b>once per port</b>, and no later write issues their DDL again.
/// </summary>
/// <remarks>
/// <para>
/// The memo is not a micro-optimisation. <c>CREATE TABLE IF NOT EXISTS</c> is a schema statement: on
/// PostgreSQL it takes a lock every other connection's DDL queues behind, and the outbox one sits on the
/// hottest path this port has, because <em>every</em> write reaches it. Issuing it per write is a contention
/// point an operator sees as a plateau under load, not as an error.
/// </para>
/// <para>
/// A repeated <c>IF NOT EXISTS</c> leaves nothing behind to count, so the memo is observed by taking the table
/// away between two writes. A port that had forgotten it already ensured puts the table back and the second
/// write lands; the real one never issues the DDL again, so the write fails loudly against the table that is
/// gone. Both halves are asserted — either alone would also pass for a port that ensures nothing at all.
/// </para>
/// </remarks>
public class EfAlvoDataEnsureOnceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Every message in an exception's chain. The engine's failure is wrapped by the port's own retry and
    /// failure families, so the table name is not on the outermost exception.
    /// </summary>
    /// <param name="exception">The exception the write raised.</param>
    private static IEnumerable<string> Chain(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            yield return current.Message;
        }
    }

    /// <summary>
    /// The outbox table is created by the first write and is not re-created by the second: with it dropped in
    /// between, the second write has nowhere to emit its event and says so.
    /// </summary>
    [Fact]
    public async Task The_outbox_table_is_ensured_by_the_first_write_and_never_again()
    {
        await using var world = await StartAsync();
        (await ExistsAsync(world, Outbox)).ShouldBeFalse("nothing has written yet");

        await world.Data.CreateAsync(Entity, Payload("first"), world.Caller, cancellationToken: Ct);
        (await ExistsAsync(world, Outbox)).ShouldBeTrue("the first write ensured it");
        await world.ExecuteAsync($"DROP TABLE {Outbox}");

        var second = await Record.ExceptionAsync(() =>
            world.Data.CreateAsync(Entity, Payload("second"), world.Caller, cancellationToken: Ct));

        // NOT just `ShouldNotBeNull`: any failure satisfies that, and a validation or authorization refusal
        // raised before the outbox insert is ever reached would keep this green while proving the opposite
        // of what the case claims. The table NAME is the contract-carrying substring here — the engine's
        // sentence around it is not asserted, only that the failure is about the table that was dropped.
        second.ShouldNotBeNull("the event had nowhere to go");
        Chain(second).ShouldContain(
            message => message.Contains(Outbox, StringComparison.Ordinal),
            "the failure must be about the dropped outbox table, not something raised before it");
        (await ExistsAsync(world, Outbox)).ShouldBeFalse("the second write issued no DDL of its own");
    }

    /// <summary>
    /// The same for the idempotency record table, which only a write carrying a token reaches — and which is
    /// therefore ensured by the first <em>tokened</em> write rather than by the first write of any kind.
    /// </summary>
    /// <remarks>
    /// The second attempt is surfaced as an <see cref="InvalidOperationException"/> rather than the engine's
    /// own failure because the idempotent path wraps a storage write failure in its retry loop and reports
    /// exhaustion inside the port's failure families.
    /// </remarks>
    [Fact]
    public async Task The_idempotency_table_is_ensured_by_the_first_tokened_write_and_never_again()
    {
        await using var world = await StartAsync();
        await world.Data.CreateAsync(Entity, Payload("untokened"), world.Caller, cancellationToken: Ct);
        (await ExistsAsync(world, Records)).ShouldBeFalse("an ordinary create records nothing");

        await world.Data.CreateAsync(Entity, Payload("first"), world.Caller, Token(), Ct);
        (await ExistsAsync(world, Records)).ShouldBeTrue("the first tokened write ensured it");
        await world.ExecuteAsync($"DROP TABLE {Records}");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            world.Data.CreateAsync(Entity, Payload("second"), world.Caller, Token(), Ct));

        (await ExistsAsync(world, Records)).ShouldBeFalse("the second write issued no DDL of its own");
    }

    private const string Outbox = "alvo_outbox";

    private const string Records = "alvo_idempotency";

    private const string Entity = WritePathFixture.Entity;

    private static async Task<bool> ExistsAsync(WritePathWorld world, string table) =>
        (long)(await world.ScalarAsync(
            $"SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '{table}'"))! == 1L;

    private static Dictionary<string, object?> Payload(string title) => WritePathFixture.CreatePayload(title);

    private static AlvoIdempotency Token() => new(Guid.NewGuid().ToString(), "fingerprint");

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());
}
