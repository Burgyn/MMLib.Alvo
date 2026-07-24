using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Unit-level coverage for <see cref="EfCoreDescriptorVersionStore"/>'s back-compatible
/// <see cref="IAppliedSchemaStore"/> surface (the one <c>SchemaMigrationRunner</c> actually calls) —
/// the cross-engine append-only contract itself is covered by
/// <c>DescriptorVersionStoreContractTests</c> against real SQLite/PostgreSQL databases.
/// </summary>
public class EfCoreDescriptorVersionStoreTests : IDisposable
{
    // A named, shared-cache SQLite in-memory database: distinct connections that share the same
    // "Data Source" name + Cache=Shared attach to the SAME in-memory database, which is what lets
    // the store's per-call connections (RelationalConnectionFactory) see each other's writes. A
    // shared-cache in-memory database is destroyed once its last connection closes, so _keepAlive
    // holds one dedicated, never-handed-out connection open for the fixture's lifetime.
    private readonly string _connectionString = $"Data Source={Guid.NewGuid():N};Mode=Memory;Cache=Shared";
    private readonly SqliteConnection _keepAlive;
    private readonly IAppliedSchemaStore _store;

    public EfCoreDescriptorVersionStoreTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();

        var connections = new RelationalConnectionFactory(() => new SqliteConnection(_connectionString));
        _store = new EfCoreDescriptorVersionStore(connections, new AlvoOptions());
    }

    public void Dispose()
    {
        _keepAlive.Dispose();
        GC.SuppressFinalize(this);
    }

    private static SchemaModel Vehicles => new([
        new EntitySchema
        {
            Name = "vehicles",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "vin", Type = FieldType.String, MaxLength = 17, Required = true },
            ],
        },
    ]);

    [Fact]
    public async Task GetCurrentAsync_for_an_unknown_project_returns_null()
    {
        var current = await _store.GetCurrentAsync("unknown", TestContext.Current.CancellationToken);

        current.ShouldBeNull();
    }

    [Fact]
    public async Task SaveAsync_then_GetCurrentAsync_round_trips_the_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var updatedAt = DateTimeOffset.Parse("2026-07-22T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        var snapshot = new AppliedSchema(Vehicles, """{"entities":[]}""", 1, updatedAt);

        await _store.SaveAsync("demo", snapshot, ct);
        var current = await _store.GetCurrentAsync("demo", ct);

        current.ShouldNotBeNull();
        current.DescriptorJson.ShouldBe(snapshot.DescriptorJson);
        current.Revision.ShouldBe(1);
        current.UpdatedAt.ShouldBe(updatedAt);

        // SchemaModel has no cross-instance structural equality (its list properties compare by
        // reference), so compare the round-tripped schema via its JSON projection instead.
        JsonSerializer.Serialize(current.Schema).ShouldBe(JsonSerializer.Serialize(snapshot.Schema));
    }

    [Fact]
    public async Task SaveAsync_twice_for_the_same_project_appends_a_new_revision_instead_of_duplicating_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveAsync("demo", new AppliedSchema(Vehicles, "{}", 1, DateTimeOffset.UtcNow), ct);

        var second = Vehicles with { Entities = [.. Vehicles.Entities, new EntitySchema { Name = "orders", Fields = [] }] };
        await _store.SaveAsync("demo", new AppliedSchema(second, "{}", 2, DateTimeOffset.UtcNow), ct);

        var current = await _store.GetCurrentAsync("demo", ct);
        current.ShouldNotBeNull();
        current.Revision.ShouldBe(2);
        current.Schema.Entities.Count.ShouldBe(2);

        var command = _keepAlive.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_descriptor_versions WHERE project = 'demo'";
        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        count.ShouldBe(2L);
    }

    [Fact]
    public async Task SaveAsync_with_a_stale_revision_throws_a_concurrency_exception()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveAsync("demo", new AppliedSchema(Vehicles, "{}", 1, DateTimeOffset.UtcNow), ct);

        // Re-saving at revision 1 again (instead of 2) means SaveAsync computes the same
        // expectedRevision (0) it already consumed — the append must be rejected, not overwrite.
        await Should.ThrowAsync<DescriptorConcurrencyException>(
            () => _store.SaveAsync("demo", new AppliedSchema(Vehicles, "{}", 1, DateTimeOffset.UtcNow), ct));
    }

    [Fact]
    public async Task A_second_EnsureAsync_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        var initializer = new SystemSchemaInitializer(_keepAlive, "alvo");

        await initializer.EnsureAsync(ct);
        await Should.NotThrowAsync(() => initializer.EnsureAsync(ct));
    }
}
