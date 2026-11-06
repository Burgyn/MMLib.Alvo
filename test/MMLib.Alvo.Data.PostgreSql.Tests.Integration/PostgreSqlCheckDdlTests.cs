using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;
using Xunit;

namespace MMLib.Alvo.Data.PostgreSql.Tests.Integration;

/// <summary>
/// The DDL a field's validation becomes on PostgreSQL (HX-11 Rulings 19, 20), frozen: the constraint inside
/// <c>CREATE TABLE</c>, and a plain <c>ALTER TABLE … ADD CONSTRAINT</c> when a validation is added to a table that
/// already exists — never <c>NOT VALID</c>, because the apply is one transaction.
/// </summary>
public sealed class PostgreSqlCheckDdlTests : IClassFixture<PostgresFixture>, IDisposable
{
    private readonly ServiceProvider _services;

    public PostgreSqlCheckDdlTests(PostgresFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        if (OperatingSystem.IsWindows())
        {
            // No container on Windows-container runners, so the connection string is empty; every fact skips in
            // VerifyPlanAsync before touching _services (the PostgreSqlGeneratedSqlSnapshotTests pattern).
            _services = new ServiceCollection().BuildServiceProvider();
            return;
        }

        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.UsePostgreSql(fixture.ConnectionString);
        builder.Services.AddAlvo();
        _services = builder.Services.BuildServiceProvider();
    }

    [Fact]
    public Task Creating_a_validated_entity_carries_its_check() => VerifyPlanAsync(new SchemaModel([]), Products(validated: true));

    [Fact]
    public Task Adding_a_validation_to_an_existing_table_adds_the_constraint_in_place() =>
        VerifyPlanAsync(Products(validated: false), Products(validated: true));

    [Fact]
    public Task Renaming_a_validated_field_recreates_its_constraint_under_the_new_name() =>
        VerifyPlanAsync(Products(validated: true), OnHand());

    [Fact]
    public Task Renaming_a_validated_entity_recreates_its_constraint_under_the_new_name() =>
        VerifyPlanAsync(Products(validated: true), StockItems());

    public void Dispose() => _services.Dispose();

    /// <summary>
    /// Plans against a database that holds <paramref name="current"/> — planning counts the rows a new validation would
    /// refuse, so it reads the table — and leaves the database empty again for the next fact.
    /// </summary>
    private async Task VerifyPlanAsync(SchemaModel current, SchemaModel desired)
    {
        Assert.SkipUnless(!OperatingSystem.IsWindows(), "PostgreSQL Testcontainers requires a Linux Docker daemon; unavailable on Windows-container runners.");
        var empty = new SchemaModel([]);
        await MigrateAsync(empty, current);
        var plan = await Migrator.PlanAsync(current, desired, new MigrationOptions(), TestContext.Current.CancellationToken);
        await MigrateAsync(current, empty);
        await Verify(string.Join("\n;\n", plan.Sql));
    }

    private async Task MigrateAsync(SchemaModel from, SchemaModel to)
    {
        var options = new MigrationOptions { AllowDestructive = true };
        var plan = await Migrator.PlanAsync(from, to, options, TestContext.Current.CancellationToken);
        await Migrator.ApplyAsync(plan, options, TestContext.Current.CancellationToken);
    }

    private ISchemaMigrator Migrator => _services.GetRequiredService<ISchemaMigrator>();

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

    private static SchemaModel StockItems() => new([
        new EntitySchema
        {
            Name = "stock_items",
            RenamedFrom = "products",
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "stock", Type = FieldType.Integer, Required = true, CheckExpression = "stock >= 0" },
            ],
        },
    ]);

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
