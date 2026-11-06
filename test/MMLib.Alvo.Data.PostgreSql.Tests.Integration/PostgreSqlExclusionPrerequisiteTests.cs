using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Npgsql;
using Xunit;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Acceptance (6): an exclusion's equality columns need <c>btree_gist</c>, so the plan installs it when the connection's role
/// may (it is trusted since PostgreSQL 13), and is refused with the fix an operator runs when it may not (HX-12 D12).
/// </summary>
public sealed class PostgreSqlExclusionPrerequisiteTests : IAsyncLifetime
{
    private const string Password = "alvo-noext";

    private readonly PostgreSqlAlvoDataFixture _fixture = new();

    public ValueTask InitializeAsync() => _fixture.InitializeAsync();

    public ValueTask DisposeAsync() => _fixture.DisposeAsync();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Without_btree_gist_and_no_right_to_install_it_the_apply_is_refused_with_the_fix()
    {
        var migrator = await MigratorAsRoleAsync(mayCreateInTheDatabase: false);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => migrator.PlanAsync(new SchemaModel([]), Bookings, new MigrationOptions(), Ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/bookings/indexes/0/exclude");
        error.Message.ShouldBe(
            "Excluding overlaps needs PostgreSQL's btree_gist extension, which this database does not have and this connection's role may not install. Nothing was applied.");
        error.FixSuggestion.ShouldBe(
            "Ask the database owner to run CREATE EXTENSION btree_gist; in this database (it ships with PostgreSQL and is trusted since PostgreSQL 13), then apply again.");
    }

    [Fact]
    public async Task With_the_right_to_install_it_the_apply_installs_btree_gist_and_the_exclusion()
    {
        var migrator = await MigratorAsRoleAsync(mayCreateInTheDatabase: true);

        var plan = await migrator.PlanAsync(new SchemaModel([]), Bookings, new MigrationOptions(), Ct);
        var applied = await migrator.ApplyAsync(plan, new MigrationOptions(), Ct);

        plan.Sql.First(statement => !statement.StartsWith("SET LOCAL", StringComparison.Ordinal))
            .ShouldBe("CREATE EXTENSION IF NOT EXISTS btree_gist");
        applied.Applied.ShouldBeTrue();
    }

    private async Task<ISchemaMigrator> MigratorAsRoleAsync(bool mayCreateInTheDatabase)
    {
        Assert.SkipUnless(_fixture.Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        var name = $"alvo_noext_{Guid.NewGuid():N}"[..30];
        await ExecuteAsync(_fixture.AdminConnectionString, [
            $"CREATE DATABASE \"{name}\"",
            $"CREATE ROLE \"{name}\" LOGIN PASSWORD '{Password}'",
            $"GRANT CONNECT ON DATABASE \"{name}\" TO \"{name}\"",
            .. mayCreateInTheDatabase ? [$"GRANT CREATE ON DATABASE \"{name}\" TO \"{name}\""] : Array.Empty<string>(),
        ]);
        var database = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString) { Database = name };
        await ExecuteAsync(database.ToString(), [$"GRANT CREATE ON SCHEMA public TO \"{name}\""]);

        var asRole = new NpgsqlConnectionStringBuilder(database.ToString()) { Username = name, Password = Password };
        return _fixture.ServicesFor(asRole.ToString()).GetRequiredService<ISchemaMigrator>();
    }

    private static async Task ExecuteAsync(string connectionString, IEnumerable<string> statements)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync(Ct);
        }
    }

    private static SchemaModel Bookings => new([new EntitySchema
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "room", Type = FieldType.String, Required = true },
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Required = true },
        ],
        Indexes = [new IndexSchema(["room"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at") }],
    }]);
}
