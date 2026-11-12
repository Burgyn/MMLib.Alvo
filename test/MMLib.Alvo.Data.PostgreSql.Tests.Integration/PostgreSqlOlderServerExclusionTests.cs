using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Npgsql;
using Xunit;
using FieldType = MMLib.Alvo.Schema.FieldType;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// Issue 477: btree_gist compares a boolean only from PostgreSQL 15, so on postgres:14 — the newest server where the
/// refusal is observable — an exclusion per a boolean field is refused at the field's pointer, naming the version that can,
/// rather than failing in DDL; every other equality type still applies. One extra container, not a version matrix: the
/// decision itself is pinned without a database in <c>PostgreSqlDialectSeamTests</c>.
/// </summary>
public sealed class PostgreSqlOlderServerExclusionTests : IClassFixture<PostgreSqlOlderServerExclusionTests.Postgres14>
{
    private readonly PostgreSqlAlvoDataFixture _fixture;

    public PostgreSqlOlderServerExclusionTests(Postgres14 server)
    {
        ArgumentNullException.ThrowIfNull(server);
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "Docker on Windows runners has no linux/amd64 postgres image, so the PostgreSQL engine cannot be started.");
        _fixture = server.Fixture;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_boolean_equality_field_is_refused_at_its_pointer_with_the_minimum_version()
    {
        var migrator = await MigratorAsync();

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => migrator.PlanAsync(new SchemaModel([]), Bookings(FieldType.Boolean), new MigrationOptions(), Ct));

        var error = refusal.Result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/entities/bookings/indexes/0/fields/1");
        error.Message.ShouldBe(
            "Excluding overlaps per 'room' compares a boolean for equality, which btree_gist cannot do on this PostgreSQL 14 server. Nothing was applied.");
        error.FixSuggestion.ShouldBe("Upgrade the server to PostgreSQL 15 or later, or remove 'room' from 'fields'.");
    }

    /// <summary>
    /// With btree_gist already installed the probe reads the extension's own operator classes from <c>pg_opclass</c> rather
    /// than the version number — and they lack the boolean one here too.
    /// </summary>
    [Fact]
    public async Task With_btree_gist_installed_its_operator_classes_refuse_the_boolean_field()
    {
        var migrator = await MigratorAsync(preinstallBtreeGist: true);

        var refusal = await Should.ThrowAsync<DescriptorValidationException>(
            () => migrator.PlanAsync(new SchemaModel([]), Bookings(FieldType.Boolean), new MigrationOptions(), Ct));

        refusal.Result.Errors.ShouldHaveSingleItem().Path.ShouldBe("/entities/bookings/indexes/0/fields/1");
    }

    [Theory]
    [InlineData(FieldType.String)]
    [InlineData(FieldType.Enum)]
    [InlineData(FieldType.Integer)]
    [InlineData(FieldType.Decimal)]
    [InlineData(FieldType.Uuid)]
    [InlineData(FieldType.Date)]
    public async Task Every_other_admitted_equality_type_applies(FieldType type)
    {
        var migrator = await MigratorAsync();

        var plan = await migrator.PlanAsync(new SchemaModel([]), Bookings(type), new MigrationOptions(), Ct);
        var applied = await migrator.ApplyAsync(plan, new MigrationOptions(), Ct);

        applied.Applied.ShouldBeTrue();
    }

    private async Task<ISchemaMigrator> MigratorAsync(bool preinstallBtreeGist = false)
    {
        Assert.SkipUnless(_fixture.Available, "Docker is unavailable on this platform, so the PostgreSQL engine cannot be started.");
        var name = $"alvo_pg14_{Guid.NewGuid():N}"[..30];
        await using (var admin = new NpgsqlConnection(_fixture.AdminConnectionString))
        {
            await admin.OpenAsync(Ct);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await create.ExecuteNonQueryAsync(Ct);
        }

        var database = new NpgsqlConnectionStringBuilder(_fixture.AdminConnectionString) { Database = name };
        if (preinstallBtreeGist)
        {
            await using var connection = new NpgsqlConnection(database.ToString());
            await connection.OpenAsync(Ct);
            await using var install = new NpgsqlCommand("CREATE EXTENSION btree_gist", connection);
            await install.ExecuteNonQueryAsync(Ct);
        }

        return _fixture.ServicesFor(database.ToString()).GetRequiredService<ISchemaMigrator>();
    }

    private static SchemaModel Bookings(FieldType roomType) => new([new EntitySchema
    {
        Name = "bookings",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "site", Type = FieldType.String, Required = true },
            new FieldSchema
            {
                Name = "room",
                Type = roomType,
                Required = true,
                EnumValues = roomType == FieldType.Enum ? ["a", "b"] : null,
            },
            new FieldSchema { Name = "starts_at", Type = FieldType.DateTime, Required = true },
            new FieldSchema { Name = "ends_at", Type = FieldType.DateTime, Required = true },
        ],
        Indexes = [new IndexSchema(["site", "room"], Unique: false) { Exclude = new ExclusionSchema("starts_at", "ends_at") }],
    }]);

    /// <summary>One postgres:14 container for the whole class, each fact in a database of its own.</summary>
    public sealed class Postgres14 : IAsyncLifetime
    {
        /// <summary>Gets the fixture over the postgres:14 container.</summary>
        internal PostgreSqlAlvoDataFixture Fixture { get; } = new() { Image = "postgres:14-alpine" };

        public ValueTask InitializeAsync() => Fixture.InitializeAsync();

        public ValueTask DisposeAsync() => Fixture.DisposeAsync();
    }
}
