using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo;
using MMLib.Alvo.Migrations;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// On SQLite a new computed field is planned as one safe <c>AddField</c> step that says it rebuilds the table, while
/// the SQL is the rebuild — the cost is said before it runs, and never as a destructive flag.
/// </summary>
public sealed class SqliteGeneratedColumnPlanTests : IDisposable
{
    private readonly ServiceProvider _services;

    public SqliteGeneratedColumnPlanTests()
    {
        var builder = new TestAlvoBuilder(new ServiceCollection());
        builder.Services.AddAlvo();
        builder.UseSqlite("Data Source=:memory:");
        _services = builder.Services.BuildServiceProvider();
    }

    [Fact]
    public async Task A_new_computed_field_is_one_safe_step_that_says_it_rebuilds_the_table()
    {
        var migrator = _services.GetRequiredService<ISchemaMigrator>();

        var plan = await migrator.PlanAsync(
            new SchemaModel([Lines(computed: false)]), new SchemaModel([Lines(computed: true)]), new MigrationOptions(),
            TestContext.Current.CancellationToken);

        var step = plan.Steps.ShouldHaveSingleItem();
        step.Change.Kind.ShouldBe(SchemaChangeKind.AddField);
        step.IsDestructive.ShouldBeFalse();
        step.Reason.ShouldBe("Rebuilds the table: copies every row under a write lock.");
        plan.Sql.ShouldContain(sql => sql.Contains("INSERT INTO", StringComparison.Ordinal), "the SQL is the rebuild");
    }

    [Fact]
    public async Task An_ordinary_new_field_carries_no_note()
    {
        var migrator = _services.GetRequiredService<ISchemaMigrator>();

        var plan = await migrator.PlanAsync(
            new SchemaModel([Lines(computed: false)]),
            new SchemaModel([Lines(computed: false) with { Fields = [.. Lines(computed: false).Fields, Note()] }]),
            new MigrationOptions(), TestContext.Current.CancellationToken);

        plan.Steps.ShouldHaveSingleItem().Reason.ShouldBeNull();
    }

    public void Dispose() => _services.Dispose();

    private static EntitySchema Lines(bool computed) => new()
    {
        Name = "lines",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "unit_price", Type = FieldType.Decimal, Precision = 18, Scale = 2, Required = true },
            new FieldSchema { Name = "amount", Type = FieldType.Integer, Required = true },
            .. computed
                ? [new FieldSchema
                {
                    Name = "total", Type = FieldType.Decimal, Precision = 18, Scale = 2, Nullable = true,
                    ComputedExpression = "unit_price * amount",
                }]
                : Array.Empty<FieldSchema>(),
        ],
    };

    private static FieldSchema Note() => new() { Name = "note", Type = FieldType.String, MaxLength = 50, Nullable = true };

    private sealed class TestAlvoBuilder(IServiceCollection services) : IAlvoBuilder
    {
        public IServiceCollection Services { get; } = services;
    }
}
