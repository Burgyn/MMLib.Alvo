using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Threading;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// End-to-end proof of runtime (dashboard-first) schema versioning — <see cref="RuntimeSchemaService"/>
/// wired exclusively through the public <c>AddAlvo().UseSqlite(...)</c> entry point, against a real
/// SQLite database file. Unit tests (<c>RuntimeSchemaServiceTests</c>) and the writer/store contract
/// suites already proved the orchestration and optimistic-lock logic against fakes/a shared fixture;
/// this proves the same behavior survives real DDL and real introspection: apply, apply-again,
/// roll back, the destructive-rollback guardrail, and a genuine two-client optimistic-lock conflict.
/// </summary>
public sealed class RuntimeSchemaSqliteIntegrationTests : IDisposable
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

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-runtime-schema-tests-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public RuntimeSchemaSqliteIntegrationTests()
    {
        var services = new ServiceCollection();
        services.AddAlvo(alvo => alvo.UseSqlite($"Data Source={_databasePath}"));
        _services = services.BuildServiceProvider();
    }

    [Fact]
    public async Task Apply_then_apply_v2_then_rollback_round_trips_through_the_live_database()
    {
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
        _services.Dispose();

        // Best-effort: the migrator/introspector/store/writer dispose their (pooling-disabled)
        // connections above, which is what actually releases the OS file handle. This is still a
        // temp file either way, so a stray lock (e.g. an antivirus scan on Windows) should not
        // fail the test — the OS reclaims temp files regardless.
        try
        {
            File.Delete(_databasePath);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private readonly record struct Outcome(bool Ok, bool Conflict);
}
