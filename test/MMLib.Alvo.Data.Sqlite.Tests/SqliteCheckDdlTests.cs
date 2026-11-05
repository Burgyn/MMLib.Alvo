using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// The DDL a field's validation becomes on SQLite (HX-11 Rulings 19, 21), frozen: the constraint inside
/// <c>CREATE TABLE</c>, and EF Core's own table rebuild when a validation is added to a table that already exists —
/// SQLite has no <c>ALTER TABLE … ADD CONSTRAINT</c>.
/// </summary>
public sealed class SqliteCheckDdlTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"alvo-sqlite-check-ddl-{Guid.NewGuid():N}.db");
    private readonly ServiceProvider _services;

    public SqliteCheckDdlTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UseSqlite($"Data Source={_databasePath}");
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    [Fact]
    public Task Creating_a_validated_entity_carries_its_check() => VerifyPlanAsync(new SchemaModel([]), Products(validated: true));

    [Fact]
    public Task Adding_a_validation_to_an_existing_table_rebuilds_it() =>
        VerifyPlanAsync(Products(validated: false), Products(validated: true));

    [Fact]
    public Task Renaming_a_validated_field_recreates_its_constraint_under_the_new_name() =>
        VerifyPlanAsync(Products(validated: true), OnHand());

    public void Dispose()
    {
        _services.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    private async Task VerifyPlanAsync(SchemaModel current, SchemaModel desired)
    {
        var plan = await _services.GetRequiredService<ISchemaMigrator>()
            .PlanAsync(current, desired, new MigrationOptions(), TestContext.Current.CancellationToken);
        await Verify(string.Join("\n;\n", plan.Sql));
    }

    private static SchemaModel Products(bool validated) => new([
        new EntitySchema
        {
            Name = "products",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = validated ? "stock >= 0" : null },
            ],
        },
    ]);

    private static SchemaModel OnHand() => new([
        new EntitySchema
        {
            Name = "products",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "on_hand", Type = FieldType.Integer, Required = true, RenamedFrom = "stock", CheckExpression = "on_hand >= 0" },
            ],
        },
    ]);

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
