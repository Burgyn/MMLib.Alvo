using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Tests.Boot;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The PostgreSQL leg of the concurrent cold start: three replicas, one empty database, all serving.
/// </summary>
/// <remarks>
/// <para>
/// Engine parity for <c>MMLib.Alvo.Data.Sqlite.Tests.ConcurrentBootTests</c>, through the same harness for the
/// same reason the differential probe is shared — the guarantee is engine-agnostic (§0 principle 3), and two
/// per-engine harnesses are how two legs stop measuring the same thing. What differs is only what an engine
/// makes the loser throw: SQLite serializes writers and surfaced <c>table "…" already exists</c>, PostgreSQL
/// surfaces the optimistic-lock loss itself. Both are answered by re-reading and deciding again.
/// </para>
/// <para>
/// Each test gets its own freshly created database inside the shared container, mirroring
/// <see cref="RuntimeSchemaIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class PostgreSqlConcurrentBootTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const int Replicas = 3;

    private readonly string _databaseName = $"alvo_concurrent_boot_{Guid.NewGuid():N}";
    private readonly string _connectionString;

    public PostgreSqlConcurrentBootTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            _connectionString = string.Empty;
            return;
        }

        CreateDatabase(fixture.ConnectionString, _databaseName);
        _connectionString = WithDatabase(fixture.ConnectionString, _databaseName);
    }

    /// <summary>
    /// Every replica serves, exactly one of them initialized, and the history holds one revision rather than
    /// one per replica.
    /// </summary>
    /// <remarks>
    /// The probe counts are the non-vacuity half: all three must have reached the schema write and met at the
    /// barrier, or nothing contended, and the two losers must have read the applied snapshot exactly twice —
    /// which is both the proof that the convergence ran and the proof that it is bounded at one retry.
    /// </remarks>
    [Fact]
    public async Task Three_hosts_cold_starting_against_one_empty_database_all_serve()
    {
        EnsureEngineAvailable();

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
    /// Converging is re-deciding, not adopting: a replica that loses while holding a <em>different</em>
    /// descriptor refuses rather than serving the winner's schema.
    /// </summary>
    /// <remarks>
    /// <c>Verify</c> is configured explicitly, because it is the mode whose decision this fact is about and it
    /// is no longer the default. Under the default <c>Apply</c> the loser applies its own descriptor over the
    /// winner's instead — still a decision rather than adoption, but a different one.
    /// </remarks>
    [Fact]
    public async Task A_loser_holding_a_different_descriptor_refuses_rather_than_adopting_the_winners_schema()
    {
        EnsureEngineAvailable();

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
    /// The PostgreSQL leg of #145: a replica holding the descriptor the database has already moved on from
    /// stands down, while the one holding the current descriptor serves.
    /// </summary>
    /// <remarks>
    /// Engine parity for
    /// <c>MMLib.Alvo.Data.Sqlite.Tests.ConcurrentBootTests.A_replica_holding_an_older_descriptor_stands_down_while_the_current_one_serves</c>,
    /// and it is worth running on both engines even though the decision is engine-agnostic by construction: the
    /// history it is decided from is read through the driver, and a driver whose <c>ListAsync</c> ordered the
    /// rows differently would answer the ordering question differently. §0 principle 3 is a claim about
    /// behaviour, not about where the code lives.
    /// </remarks>
    [Fact]
    public async Task A_replica_holding_an_older_descriptor_stands_down_while_the_current_one_serves()
    {
        EnsureEngineAvailable();

        var race = await RaceAsync(
            [ConcurrentColdStart.Descriptor, ConcurrentColdStart.DriftedDescriptor],
            deployedBefore: [ConcurrentColdStart.Descriptor, ConcurrentColdStart.DriftedDescriptor]);

        race.RecordedRevisions.ShouldBe(
            [1, 2], "the older replica must not append a third revision undoing the second");
        race.AppliedFields.ShouldContain("depots.region");
        race.Replicas.Sum(replica => replica.SchemaWrites).ShouldBe(
            0, "the older replica must be stopped before it contends for the DDL at all");

        race.Replicas.Single(replica => replica.Serving).AppliedRevision.ShouldBe(2);

        var stoodDown = race.Replicas.Single(replica => !replica.Serving);
        stoodDown.Failure.ShouldBeNull(
            $"a pod that is one deploy behind must be drained, not crash-looped. {stoodDown.Explain()}");
        stoodDown.Phase.ShouldBe(AlvoBootPhase.Failed);

        var reason = stoodDown.PublishedFailure.ShouldNotBeNull();
        reason.ShouldContain("revision 1");
        reason.ShouldContain("revision 2");
    }

    private Task<ColdStartRace> RaceAsync(
        IReadOnlyList<string> descriptorPerReplica,
        AlvoSchemaStartupMode? startup = null,
        IReadOnlyList<string>? deployedBefore = null) =>
        ConcurrentColdStart.RaceAsync(
            alvo => alvo.UsePostgreSql(_connectionString),
            descriptorPerReplica,
            TestContext.Current.CancellationToken,
            startup,
            deployedBefore);

    private static void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    private static void CreateDatabase(string adminConnectionString, string databaseName)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    private static string WithDatabase(string connectionString, string databaseName) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ToString();

    public void Dispose()
    {
        // The container's disposal (PostgresFixture.DisposeAsync) tears down every database created inside it,
        // including this one — nothing to drop here explicitly.
        GC.SuppressFinalize(this);
    }
}
