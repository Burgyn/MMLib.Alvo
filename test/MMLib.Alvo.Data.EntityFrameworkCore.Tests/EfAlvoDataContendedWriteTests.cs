using System.Diagnostics;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The attempt loop an idempotent write runs when storage refuses it: how many attempts it makes, that it
/// waits longer between each one, and that it stops.
/// </summary>
/// <remarks>
/// <para>
/// Every number here is load-bearing and none of it is observable from a suite that cannot make one statement
/// fail. One attempt too few turns ordinary contention into a failed request — the retry exists because a
/// rival's transaction is a handful of statements, so the loser only has to outlast it. One attempt too many,
/// or a loop that never gives up, turns a permanently failing write into a hung request instead of an answer.
/// And a backoff that does not grow is a hot loop re-asking a question whose answer has not changed.
/// </para>
/// <para>
/// The injected failure is <c>SQLITE_BUSY</c>, never a constraint code, because the two are answered
/// differently on purpose: a constraint violation names a field and leaves on the first attempt, and only a
/// storage write failure reaches the loop at all.
/// </para>
/// </remarks>
public class EfAlvoDataContendedWriteTests
{
    /// <summary>What <c>EfAlvoData.ContendedCreateAttempts</c> is, as this suite reads it.</summary>
    private static int Attempts => 10;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// Storage that refuses every attempt is attempted exactly <see cref="Attempts"/> times and then surfaces
    /// as this port's own failure — not one attempt fewer, not one more, and not forever.
    /// </summary>
    [Fact]
    public async Task A_write_storage_always_refuses_is_attempted_ten_times_and_then_gives_up()
    {
        await using var world = await StartAsync();
        RefuseInserts(world, Attempts);

        await Should.ThrowAsync<InvalidOperationException>(() => world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("contended"), world.Caller, Token(), Ct));

        world.Statements.Failed.ShouldBe(Attempts);
    }

    /// <summary>
    /// The pause between attempts grows with the attempt number. Without it — or with it shrinking — ten
    /// attempts complete in the time one rival transaction takes, so every one of them loses the same race.
    /// </summary>
    /// <remarks>
    /// A floor rather than an equality: the ten attempts do real work on top of the 450 ms of waiting, so the
    /// measurement can only be too high. A loop that divides by the attempt number instead waits about 25 ms
    /// in total, and a loop that does not wait waits none.
    /// </remarks>
    [Fact]
    public async Task The_wait_between_attempts_grows_rather_than_shrinking()
    {
        await using var world = await StartAsync();
        RefuseInserts(world, Attempts);
        var elapsed = Stopwatch.StartNew();

        await Should.ThrowAsync<InvalidOperationException>(() => world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("contended"), world.Caller, Token(), Ct));

        elapsed.Elapsed.ShouldBeGreaterThan(TimeSpan.FromMilliseconds(300));
    }

    /// <summary>
    /// A write storage refuses once is attempted again and commits — the case the loop exists for, and the
    /// one a loop that gives up on its first failure turns into a 500 under ordinary contention.
    /// </summary>
    [Fact]
    public async Task A_write_storage_refuses_once_is_attempted_again_and_commits()
    {
        await using var world = await StartAsync();
        RefuseInserts(world, 1);

        var created = await world.Data.CreateAsync(
            WritePathFixture.Entity, WritePathFixture.CreatePayload("contended"), world.Caller, Token(), Ct);

        created["title"].ShouldBe("contended");
        world.Statements.Failed.ShouldBe(1);
        (await world.ScalarAsync("SELECT COUNT(*) FROM \"note\"")).ShouldBe(1L);
    }

    private static void RefuseInserts(WritePathWorld world, int times)
    {
        world.Statements.FailWhenContains = """INSERT INTO "note" """;
        world.Statements.FailuresLeft = times;
    }

    private static AlvoIdempotency Token() => new(Guid.NewGuid().ToString(), "fingerprint");

    private static Task<WritePathWorld> StartAsync() =>
        WritePathWorld.StartAsync(WritePathFixture.Descriptor(), WritePathFixture.Schema());
}
