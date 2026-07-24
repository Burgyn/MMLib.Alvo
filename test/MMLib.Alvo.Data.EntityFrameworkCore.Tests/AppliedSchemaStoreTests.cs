using Microsoft.Data.Sqlite;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using System.Text.Json;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

public class AppliedSchemaStoreTests : IDisposable
{
    // A single shared, already-open connection: ":memory:" SQLite DBs live only as long as their
    // one connection stays open, so it must be kept open and reused across calls for the table
    // (and its rows) to persist between them — same pattern as EfCoreSchemaMigratorApplyTests.
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly AppliedSchemaStore _store;

    public AppliedSchemaStoreTests()
    {
        _connection.Open();
        _store = new AppliedSchemaStore(_connection, new AlvoOptions());
    }

    public void Dispose()
    {
        _store.Dispose();
        _connection.Dispose();
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
    public async Task SaveAsync_twice_for_the_same_project_upserts_instead_of_duplicating()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.SaveAsync("demo", new AppliedSchema(Vehicles, "{}", 1, DateTimeOffset.UtcNow), ct);

        var second = Vehicles with { Entities = [.. Vehicles.Entities, new EntitySchema { Name = "orders", Fields = [] }] };
        await _store.SaveAsync("demo", new AppliedSchema(second, "{}", 2, DateTimeOffset.UtcNow), ct);

        var current = await _store.GetCurrentAsync("demo", ct);
        current.ShouldNotBeNull();
        current.Revision.ShouldBe(2);
        current.Schema.Entities.Count.ShouldBe(2);

        var command = _connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM alvo_applied_schema WHERE project = 'demo'";
        var count = (long)(await command.ExecuteScalarAsync(ct))!;
        count.ShouldBe(1L);
    }

    [Fact]
    public async Task A_second_EnsureAsync_is_a_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        var initializer = new SystemSchemaInitializer(_connection, "alvo");

        await initializer.EnsureAsync(ct);
        await Should.NotThrowAsync(() => initializer.EnsureAsync(ct));
    }
}
