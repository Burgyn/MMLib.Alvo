using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Boot;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// Three replicas cold-starting against one empty SQLite file: the ordinary first deployment of a replica set,
/// and the scenario that either converges or crash-loops.
/// </summary>
/// <remarks>
/// <para>
/// <b>SQLite is the harder engine here, so it is the leg that must not be skipped.</b> One writer at a time
/// means the losers can fail on lock contention rather than on the optimistic-lock check — a different failure
/// needing different handling — which is exactly why this fact is measured on a real file rather than inferred
/// from the PostgreSQL leg.
/// </para>
/// <para>
/// The harness (<see cref="ConcurrentColdStart"/>) forces the collision with a barrier in front of the schema
/// write and reports whether the replicas actually met, so these facts cannot quietly become sequential.
/// </para>
/// </remarks>
public sealed class ConcurrentBootTests : IDisposable
{
    private const int Replicas = 3;

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"alvo-concurrent-boot-{Guid.NewGuid():N}.db");

    /// <summary>
    /// The fact Task 5 exists for: every replica serves, exactly one of them initialized, and the history holds
    /// one revision rather than one per replica.
    /// </summary>
    /// <remarks>
    /// The probe counts are the non-vacuity half. All three replicas must have <em>reached</em> the schema
    /// write and met at the barrier, or nothing contended; and the two that lost must have read the applied
    /// snapshot exactly twice — once before the race and once to converge — which is both the proof that the
    /// convergence ran and the proof that it is bounded at one retry rather than looping.
    /// </remarks>
    [Fact]
    public async Task Three_hosts_cold_starting_against_one_empty_database_all_serve()
    {
        var race = await RaceAsync([.. Enumerable.Repeat(ConcurrentColdStart.Descriptor, Replicas)]);

        foreach (var replica in race.Replicas)
        {
            replica.Failure.ShouldBeNull(
                $"a replica must not crash-loop on an ordinary cold start. {replica.Explain()}");
            replica.Phase.ShouldBe(AlvoBootPhase.Ready);
            replica.AppliedRevision.ShouldBe(1);
            replica.Rendezvoused.ShouldBeTrue("a race nobody met is not a race, and this fact would prove nothing");
            replica.SchemaWrites.ShouldBe(1, "every replica of an empty database must decide to initialize it");
        }

        race.Replicas.Sum(replica => replica.AppliedSchemaWrites).ShouldBe(
            1, "exactly one replica may create the schema; the others must observe its outcome");
        race.RecordedRevisions.ShouldBe([1]);
        race.Replicas.Count(replica => replica.AppliedSchemaReads == 2).ShouldBe(
            Replicas - 1, "each loser re-reads the applied snapshot exactly once — the retry is bounded at one");
    }

    /// <summary>
    /// Converging is re-deciding, not adopting: a replica that loses the race while holding a
    /// <em>different</em> descriptor refuses to start rather than serving the winner's schema.
    /// </summary>
    /// <remarks>
    /// A rolling deploy can put two descriptors on the same database at the same moment, and whichever wins,
    /// the loser is now looking at ordinary drift. Silently accepting the winner's schema would make the
    /// process serve rules compiled against a schema it never agreed to, which is the failure mode the whole
    /// startup mode exists to prevent — so the loser's second decision is the same decision any drifting boot
    /// makes.
    /// <para>
    /// <c>Verify</c> is configured explicitly, because it is the mode whose decision this fact is about and it
    /// is no longer the default. Under the default <c>Apply</c> the loser applies its own descriptor over the
    /// winner's instead — still a decision rather than adoption, but a different one, and a fact that named no
    /// mode would have quietly become a fact about that.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_loser_holding_a_different_descriptor_refuses_rather_than_adopting_the_winners_schema()
    {
        var race = await RaceAsync(
            [ConcurrentColdStart.Descriptor, ConcurrentColdStart.DriftedDescriptor],
            AlvoSchemaStartupMode.Verify);

        race.Replicas.Count(replica => replica.Serving).ShouldBe(
            1, "two descriptors cannot both be the schema of one database");
        race.RecordedRevisions.ShouldBe([1], "the refused replica must not have appended its own revision");

        var refused = race.Replicas.Single(replica => !replica.Serving);
        refused.Failure.ShouldBeOfType<AlvoStartupRefusedException>(
            "the loser must reach a decision about the winner's schema, not die of the lost race itself. "
            + refused.Explain());
        refused.Phase.ShouldBe(AlvoBootPhase.Failed);
        refused.AppliedRevision.ShouldBeNull();
        refused.AppliedSchemaReads.ShouldBe(2, "the refusal must be reached by re-reading, not by guessing");
    }

    private Task<ColdStartRace> RaceAsync(
        IReadOnlyList<string> descriptorPerReplica, AlvoSchemaStartupMode? startup = null) =>
        ConcurrentColdStart.RaceAsync(
            alvo => alvo.UseSqlite($"Data Source={_databasePath}"),
            descriptorPerReplica,
            TestContext.Current.CancellationToken,
            startup);

    public void Dispose()
    {
        // Best-effort: every replica's container is disposed inside the race, which releases the
        // (pooling-disabled) connections and with them the OS file handle. This is a temp file either way.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }
}
