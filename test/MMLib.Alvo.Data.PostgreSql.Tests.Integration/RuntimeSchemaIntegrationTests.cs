using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Npgsql;
using System.Threading;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// End-to-end proof of runtime (dashboard-first) schema versioning — <see cref="RuntimeSchemaService"/>
/// wired exclusively through the public <c>AddAlvo().UsePostgreSql(...)</c> entry point, against a
/// real PostgreSQL server. The engine-parity leg to <c>RuntimeSchemaSqliteIntegrationTests</c>: apply,
/// apply-again, roll back, the destructive-rollback guardrail, and a genuine two-client
/// optimistic-lock conflict, all verified against what <see cref="ISchemaIntrospector"/> actually
/// sees in the live database.
/// </summary>
/// <remarks>
/// The Testcontainers server is shared for the class via <see cref="PostgresFixture"/>; each test
/// instance gets its own freshly-created database, mirroring <see cref="PostgreSqlRuntimeSchemaWriterTests"/>
/// and <see cref="PostgreSqlDescriptorVersionStoreTests"/>.
/// </remarks>
public sealed class RuntimeSchemaIntegrationTests : IClassFixture<PostgresFixture>, IDisposable
{
    private const string TasksV1 = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    // Adds an optional field relative to TasksV1 — an AddField step, always non-destructive.
    private const string TasksV2 = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "notes": { "type": "string" }
              }
            }
          }
        }
        """;

    // A second, independent optional-field addition over TasksV1 — used opposite TasksV2 in the
    // two-client race so both candidates are valid, non-destructive, and derived from revision 1.
    private const string TasksV2b = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "priority": { "type": "string" }
              }
            }
          }
        }
        """;

    // Adds a *required* field relative to TasksV1. Adding it is non-destructive (AddField), but
    // rolling back FROM this TO TasksV1 drops that field, which IS destructive.
    private const string TasksV1WithExtra = """
        {
          "apiVersion": "alvo.dev/v1",
          "name": "demo",
          "entities": {
            "tasks": {
              "fields": {
                "title": { "type": "string", "required": true },
                "assignee": { "type": "string", "required": true }
              }
            }
          }
        }
        """;

    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";
    private readonly ServiceProvider _services;

    public RuntimeSchemaIntegrationTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (Windows-container runners can't run the
            // Linux postgres:16-alpine image), so every test below calls EnsureEngineAvailable()
            // as its first statement and skips before any of these services are touched.
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        CreateDatabase(fixture.ConnectionString, _databaseName);
        var connectionString = WithDatabase(fixture.ConnectionString, _databaseName);

        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UsePostgreSql(connectionString));
        _services = services.BuildServiceProvider();
    }

    private static void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    [Fact]
    public async Task Apply_then_apply_v2_then_rollback_round_trips_through_the_live_database()
    {
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var service = _services.GetRequiredService<RuntimeSchemaService>();
        var introspector = _services.GetRequiredService<ISchemaIntrospector>();

        await service.ApplyAsync("demo", TasksV1, expectedRevision: 0, new MigrationOptions(), ct);
        var afterV1 = await introspector.IntrospectAsync(ct);
        var tasksV1 = afterV1.Entities.Single(e => e.Name == "tasks");
        tasksV1.Fields.Select(f => f.Name).ShouldContain("title");
        tasksV1.Fields.Select(f => f.Name).ShouldNotContain("notes");

        await service.ApplyAsync("demo", TasksV2, expectedRevision: 1, new MigrationOptions(), ct);
        var afterV2 = await introspector.IntrospectAsync(ct);
        var tasksV2 = afterV2.Entities.Single(e => e.Name == "tasks");
        tasksV2.Fields.Select(f => f.Name).ShouldContain("notes");

        var rolledBack = await service.RollbackAsync(
            "demo", targetRevision: 1, new MigrationOptions { AllowDestructive = true }, ct);

        rolledBack.Revision.ShouldBe(3);
        rolledBack.RolledBackFrom.ShouldBe(1);

        var afterRollback = await introspector.IntrospectAsync(ct);
        var tasksRolledBack = afterRollback.Entities.Single(e => e.Name == "tasks");
        tasksRolledBack.Fields.Select(f => f.Name).ShouldContain("title");
        tasksRolledBack.Fields.Select(f => f.Name).ShouldNotContain("notes");
    }

    [Fact]
    public async Task Destructive_rollback_without_AllowDestructive_is_refused_and_leaves_the_database_unchanged()
    {
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var service = _services.GetRequiredService<RuntimeSchemaService>();
        var introspector = _services.GetRequiredService<ISchemaIntrospector>();

        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), ct);
        await service.ApplyAsync("demo", TasksV1WithExtra, 1, new MigrationOptions(), ct); // adds required "assignee"

        await Should.ThrowAsync<DestructiveChangeNotAllowedException>(
            () => service.RollbackAsync("demo", targetRevision: 1, new MigrationOptions(), ct));

        // The refused rollback must not have partially applied: "assignee" (which the reverse
        // plan would have dropped) is still there.
        var unchanged = await introspector.IntrospectAsync(ct);
        var tasks = unchanged.Entities.Single(e => e.Name == "tasks");
        tasks.Fields.Select(f => f.Name).ShouldContain("assignee");
    }

    [Fact]
    public async Task Two_concurrent_appends_at_the_same_expected_revision_yield_exactly_one_winner()
    {
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var service = _services.GetRequiredService<RuntimeSchemaService>();
        var store = _services.GetRequiredService<IDescriptorVersionStore>();

        // Warm up: lands rev 1 and forces any one-time setup (e.g. lazy table creation) to happen
        // before the race below, so it cannot accidentally serialize the two racing applies.
        await service.ApplyAsync("demo", TasksV1, 0, new MigrationOptions(), ct);

        // Two independent, non-destructive AddField candidates, both derived from rev 1. A Barrier
        // releases both onto separate threadpool threads at (as close to) the same instant — a
        // genuine race, not two sequential calls that merely avoid an explicit await in between.
        using var barrier = new Barrier(2);
        var first = Task.Run(() => RaceAsync(service, TasksV2, barrier, ct), ct);
        var second = Task.Run(() => RaceAsync(service, TasksV2b, barrier, ct), ct);

        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Ok).ShouldBe(1);
        outcomes.Count(o => o.Conflict).ShouldBe(1);

        (await store.ListAsync("demo", ct)).Count.ShouldBe(2);
    }

    private static async Task<Outcome> RaceAsync(RuntimeSchemaService service, string descriptorJson, Barrier barrier, CancellationToken ct)
    {
        barrier.SignalAndWait(ct);
        try
        {
            await service.ApplyAsync("demo", descriptorJson, expectedRevision: 1, new MigrationOptions(), ct);
            return new Outcome(Ok: true, Conflict: false);
        }
        catch (DescriptorConcurrencyException)
        {
            return new Outcome(Ok: false, Conflict: true);
        }
    }

    public void Dispose()
    {
        // The container's disposal (PostgresFixture.DisposeAsync) tears down every database
        // created inside it, including this one — nothing to drop here explicitly.
        _services.Dispose();
        GC.SuppressFinalize(this);
    }

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

    private readonly record struct Outcome(bool Ok, bool Conflict);
}
