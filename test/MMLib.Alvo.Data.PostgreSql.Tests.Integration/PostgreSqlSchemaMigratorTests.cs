using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Migrations;
using Npgsql;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Runs the full <see cref="SchemaMigratorContractTests"/> suite against a real PostgreSQL
/// server, wired exclusively through the public
/// <see cref="AlvoPostgreSqlBuilderExtensions.UsePostgreSql"/> entry point — the same path a host
/// application would use.
/// </summary>
/// <remarks>
/// The server itself (the Testcontainers container) is shared for the whole class via
/// <see cref="PostgresFixture"/> — starting a container per test would be needlessly slow. Each
/// test instance, however, still needs its own isolated database: the contract suite creates a
/// "vehicles" table from an empty schema in more than one test, and a shared database would make
/// the second test to run fail with "relation already exists". A fresh, cheaply-created
/// <c>CREATE DATABASE</c> per test instance (mirroring the Sqlite suite's fresh-file-per-instance
/// isolation) gives that without paying for a second container.
/// </remarks>
public sealed class PostgreSqlSchemaMigratorTests : SchemaMigratorContractTests, IClassFixture<PostgresFixture>, IDisposable
{
    private const string RowId = "11111111-1111-1111-1111-111111111111";

    private readonly string _databaseName = $"alvo_test_{Guid.NewGuid():N}";
    private readonly string _connectionString;
    private readonly ServiceProvider _services;

    public PostgreSqlSchemaMigratorTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // The fixture never started a container (Windows-container runners can't run the
            // Linux postgres:16-alpine image), so fixture.ConnectionString is empty here — every
            // test below calls EnsureEngineAvailable() as its first statement and skips before
            // touching _services/_connectionString.
            _connectionString = string.Empty;
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        CreateDatabase(fixture.ConnectionString, _databaseName);
        _connectionString = WithDatabase(fixture.ConnectionString, _databaseName);

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(_connectionString);
        _services = builder.Services.BuildServiceProvider();
    }

    protected override void EnsureEngineAvailable() =>
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");

    protected override ISchemaMigrator CreateMigrator() => _services.GetRequiredService<ISchemaMigrator>();

    protected override Task<SchemaModel> IntrospectAsync() =>
        _services.GetRequiredService<ISchemaIntrospector>().IntrospectAsync();

    private static SchemaModel Empty() => new([]);

    private static SchemaModel OneField(string fieldName, FieldType type = FieldType.String) => new([
        new EntitySchema
        {
            Name = "vehicles",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = fieldName, Type = type, Nullable = true },
            ],
        },
    ]);

    [Fact]
    public async Task Drop_and_add_same_type_is_destructive_and_does_not_carry_data_over()
    {
        // Drop undeclared "a" + add same-type "d": EF guesses a single RenameColumn a->d. Accepting
        // it would bypass AllowDestructive and carry "a"'s data into the unrelated "d". The splitter
        // must reclassify it into a destructive Drop + a fresh Add. (Finding A, PostgreSQL leg.)
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var migrator = CreateMigrator();
        var before = OneField("a");
        var after = OneField("d");

        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, a) VALUES ('{RowId}', 'hello')", ct);

        var plan = await migrator.PlanAsync(before, after, new MigrationOptions(), ct);

        plan.HasDestructiveChanges.ShouldBeTrue();
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.DropField && s.Change.Field == "a");
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.AddField && s.Change.Field == "d");
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.RenameField);

        (await migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = false }, ct)).Applied.ShouldBeFalse();

        (await migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = true }, ct)).Applied.ShouldBeTrue();

        var vehicles = (await IntrospectAsync()).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "d");
        vehicles.Fields.ShouldNotContain(f => f.Name == "a");

        (await QueryScalarAsync($"SELECT d FROM vehicles WHERE id = '{RowId}'", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Drop_and_add_different_type_applies_cleanly()
    {
        // Drop "a" (text) + add "n" (bigint): two ops. Whole-plan SQL generation must apply cleanly
        // (the per-op shortcut is the SQLite failure mode; Postgres is the parity leg). (Finding B.)
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var migrator = CreateMigrator();
        var before = OneField("a");
        var after = OneField("n", FieldType.Integer);

        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, a) VALUES ('{RowId}', 'hello')", ct);

        var plan = await migrator.PlanAsync(before, after, new MigrationOptions(), ct);
        (await migrator.ApplyAsync(plan, new MigrationOptions { AllowDestructive = true }, ct)).Applied.ShouldBeTrue();

        var vehicles = (await IntrospectAsync()).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "n");
        vehicles.Fields.ShouldNotContain(f => f.Name == "a");
    }

    [Fact]
    public async Task Declared_rename_still_preserves_data()
    {
        // Regression guard: a DECLARED rename must stay a genuine, data-preserving rename after the
        // Finding-A fix.
        EnsureEngineAvailable();
        var ct = TestContext.Current.CancellationToken;
        var migrator = CreateMigrator();
        var before = OneField("colour");
        var after = new SchemaModel([
            new EntitySchema
            {
                Name = "vehicles",
                Fields =
                [
                    new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                    new FieldSchema { Name = "color", Type = FieldType.String, Nullable = true, RenamedFrom = "colour" },
                ],
            },
        ]);

        await migrator.ApplyAsync(await migrator.PlanAsync(Empty(), before, new MigrationOptions(), ct), new MigrationOptions(), ct);
        await ExecAsync($"INSERT INTO vehicles (id, colour) VALUES ('{RowId}', 'red')", ct);

        var plan = await migrator.PlanAsync(before, after, new MigrationOptions(), ct);
        plan.HasDestructiveChanges.ShouldBeFalse();
        plan.Steps.ShouldContain(s => s.Change.Kind == SchemaChangeKind.RenameField && s.Change.Field == "color");
        plan.Steps.ShouldNotContain(s => s.Change.Kind == SchemaChangeKind.DropField);

        (await migrator.ApplyAsync(plan, new MigrationOptions(), ct)).Applied.ShouldBeTrue();

        var vehicles = (await IntrospectAsync()).Entities.ShouldHaveSingleItem();
        vehicles.Fields.ShouldContain(f => f.Name == "color");
        (await QueryScalarAsync($"SELECT color FROM vehicles WHERE id = '{RowId}'", ct)).ShouldBe("red");
    }

    private async Task ExecAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct);
    }

    private async Task<object?> QueryScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(_connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync(ct);
        return value is DBNull ? null : value;
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

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
