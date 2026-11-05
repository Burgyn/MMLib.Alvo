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

    public void Dispose() => _services.Dispose();

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
