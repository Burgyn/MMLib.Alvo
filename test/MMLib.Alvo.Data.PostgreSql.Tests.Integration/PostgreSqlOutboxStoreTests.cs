using MMLib.Alvo.Testing.Events;
using MMLib.Alvo.Tests.Data;

using Npgsql;

using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the whole <see cref="OutboxStoreContractTests"/> suite against a real PostgreSQL server, and adds the
/// one fact no shape assertion can reach: two claimants racing one queue.
/// </summary>
/// <remarks>
/// The server is shared for the whole class via <see cref="PostgresFixture"/>; the <em>database</em> is fresh
/// per test instance, mirroring <see cref="PostgreSqlOutboxTableTests"/>'s isolation, because the inherited
/// facts count claimed entries and one test's rows must not be another's starting state. Nothing is dropped
/// here: the container's own disposal tears down every database created inside it.
/// </remarks>
public sealed class PostgreSqlOutboxStoreTests : OutboxStoreContractTests, IClassFixture<PostgresFixture>
{
    private readonly string _connectionString = string.Empty;

    public PostgreSqlOutboxStoreTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (a Windows-container runner cannot run the Linux
            // postgres:16-alpine image), so every fact skips on EnsureEngineAvailable() below before it ever
            // asks for a connection.
            return;
        }

        var databaseName = $"alvo_outbox_store_{Guid.NewGuid():N}";
        CreateDatabase(fixture.ConnectionString, databaseName);
        _connectionString = WithDatabase(fixture.ConnectionString, databaseName);
    }

    /// <summary>
    /// With no <c>SKIP LOCKED</c>, the second claimant blocks on the first's row locks and must then claim
    /// <b>nothing</b> — not the entries the first one just took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one fact in the suite that would have caught the claim statement's original shape, and the reason
    /// the outer <c>WHERE</c> repeats the subquery's claimability predicate. Measured without that repetition
    /// (spike Q4): <em>"A claimed 10, B claimed 10, overlap 10 (must be 0); rows with attempts &gt; 1: 10"</em>
    /// — every entry delivered twice. A shape assertion cannot notice a row claimed twice, and this is
    /// PostgreSQL's leg because the mechanism is PostgreSQL's: under <c>READ COMMITTED</c>, EvalPlanQual
    /// re-checks the outer <c>WHERE</c> and nothing else once the block clears.
    /// </para>
    /// <para>
    /// <c>MAX(attempts)</c> is asserted as well as the overlap, because it is the half of Q4's finding that
    /// survives even when the overlap looks empty: a double claim increments the counter whether or not both
    /// callers see the rows.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task A_second_claimant_claims_nothing_rather_than_the_same_rows()
    {
        EnsureEngineAvailable();
        await using var world = await ConcreteWorldAsync();
        await world.SeedAsync(count: 10);

        var (first, second) = await world.TwoConcurrentClaimsAsync(batchSize: 10);

        first.Count.ShouldBe(10);
        second.ShouldBeEmpty(
            "no SKIP LOCKED means the loser BLOCKS and then re-checks; with the claimability predicate "
            + "only in the subquery it re-claimed all 10 and attempts reached 2 on every row (spike Q4)");
        (await world.MaxAttemptsAsync()).ShouldBe(1);
    }

    protected override async Task<IOutboxStoreWorld> WorldAsync() => await ConcreteWorldAsync();

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(
            !OperatingSystem.IsWindows(),
            "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    private Task<OutboxStoreWorld> ConcreteWorldAsync() =>
        OutboxStoreWorld.StartAsync(() => new NpgsqlConnection(_connectionString));

    private static void CreateDatabase(string adminConnectionString, string databaseName)
    {
        using var connection = new NpgsqlConnection(adminConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        command.ExecuteNonQuery();
    }

    private static string WithDatabase(string connectionString, string databaseName) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Database = databaseName }.ConnectionString;
}
