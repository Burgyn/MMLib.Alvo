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

    /// <summary>
    /// The divergent-additive rolling deploy — one pod adds <c>region</c>, the other adds <c>city</c>, both under
    /// the default <c>Apply</c> — ends on <b>one</b> descriptor's schema, never on the union of the two.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This measures a hazard that was published as measured and is not reachable.</b> The claim (this
    /// repository's own <c>docs/architecture/host.md</c> and #145) was that neither plan drops anything, so both
    /// apply and the database ends up with both columns — a schema no deployed descriptor declares. It does not:
    /// the loser diffs its own descriptor against a snapshot carrying the winner's field, which it does not
    /// declare, so its plan drops it, and the always-on destructive gate refuses a drop in <em>every</em> mode.
    /// </para>
    /// <para>
    /// The applied field list is the assertion that distinguishes the two stories — the revision history alone
    /// cannot, because <c>[1]</c> is also what a hypothetical single merged apply would leave. What is really
    /// reachable under <c>Apply</c>, and what #145 is about, is the <em>superset</em> case: a descriptor that is a
    /// strict superset applies over the other and the pod holding the subset then refuses at its next start.
    /// </para>
    /// <para>
    /// No mode is configured, so this is the product's own default. Which replica wins is decided by the race, so
    /// every assertion here is symmetric in the two descriptors.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Two_replicas_adding_different_fields_end_on_one_descriptors_schema_not_the_union()
    {
        var race = await RaceAsync([ConcurrentColdStart.DriftedDescriptor, ConcurrentColdStart.DivergentDescriptor]);

        race.Replicas.Count(replica => replica.Serving).ShouldBe(
            1, "the loser's own descriptor drops the winner's field, which no mode allows");
        race.RecordedRevisions.ShouldBe([1]);
        race.AppliedFields.ShouldContain("depots.code");
        race.AppliedFields.Count(field => field is "depots.region" or "depots.city").ShouldBe(
            1,
            "the database must end on one descriptor's schema; holding both fields would be a schema no deployed "
            + $"descriptor declares. Applied: {string.Join(", ", race.AppliedFields)}");

        var refused = race.Replicas.Single(replica => !replica.Serving);
        refused.Failure.ShouldBeOfType<AlvoStartupRefusedException>(refused.Explain())
            .Message.ShouldContain("destructive");
    }

    /// <summary>
    /// #145's acceptance criterion: two replicas of a rolling deploy, one of them holding the descriptor the
    /// database has already moved on from, and the older one stands down instead of applying its schema over
    /// the newer one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The difference from the facts above is that the database already holds a <em>history</em>.</b> Racing
    /// an empty database, both descriptors are new and the loser is refused by the destructive gate; that is the
    /// cold-start case and it was never the defect. The reachable one is the ordinary rolling deploy: revision 2
    /// is applied, a pod of the old ReplicaSet restarts, and under the default <c>Apply</c> nothing but the
    /// ordering compares the two generations.
    /// </para>
    /// <para>
    /// <b>Nobody reaching the schema write is the assertion, not an accident.</b> The old fix for this shape was
    /// "the loser's plan is destructive, so it is refused" — which happens <em>at</em> the write's door and only
    /// when the change discards something. Here the outcome is decided before either replica contends, which is
    /// why <c>Rendezvoused</c> is deliberately not asserted: the race the harness exists to force is the thing
    /// that has been prevented, and asserting the barrier was met would demand the defect back.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_replica_holding_an_older_descriptor_stands_down_while_the_current_one_serves()
    {
        var race = await RaceAsync(
            [ConcurrentColdStart.Descriptor, ConcurrentColdStart.DriftedDescriptor],
            deployedBefore: [ConcurrentColdStart.Descriptor, ConcurrentColdStart.DriftedDescriptor]);

        race.RecordedRevisions.ShouldBe(
            [1, 2], "the older replica must not append a third revision undoing the second");
        race.AppliedFields.ShouldContain(
            "depots.region", "the database must still be on the newer descriptor's schema");
        race.Replicas.Sum(replica => replica.SchemaWrites).ShouldBe(
            0, "neither replica may reach the schema write: one has nothing to do and the other must be stopped "
            + "before it contends for the DDL at all");

        var serving = race.Replicas.Single(replica => replica.Serving);
        serving.AppliedRevision.ShouldBe(2, serving.Explain());

        var stoodDown = race.Replicas.Single(replica => !replica.Serving);
        stoodDown.Failure.ShouldBeNull(
            $"a pod that is one deploy behind must be drained, not crash-looped. {stoodDown.Explain()}");
        stoodDown.Phase.ShouldBe(AlvoBootPhase.Failed);
        stoodDown.AppliedRevision.ShouldBeNull();

        var reason = stoodDown.PublishedFailure.ShouldNotBeNull(
            "a replica that stands down throws nothing, so the published reason is the only diagnosis an "
            + "operator gets");
        reason.ShouldContain("revision 1");
        reason.ShouldContain("revision 2");
    }

    private Task<ColdStartRace> RaceAsync(
        IReadOnlyList<string> descriptorPerReplica,
        AlvoSchemaStartupMode? startup = null,
        IReadOnlyList<string>? deployedBefore = null) =>
        ConcurrentColdStart.RaceAsync(
            alvo => alvo.UseSqlite($"Data Source={_databasePath}"),
            descriptorPerReplica,
            TestContext.Current.CancellationToken,
            startup,
            deployedBefore);

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
