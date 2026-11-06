using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// A rollup filter's SQL depends on the child's field types — on SQLite a <c>decimal</c> comparison is repaired with
/// <c>CAST(… AS REAL)</c>, an <c>integer</c> one is not — so the one recompute a process holds must render a new schema
/// revision afresh rather than reuse what an earlier revision of the same child rendered (HX-11 Ruling 70).
/// </summary>
public sealed class RollupFilterRevisionTests
{
    private static readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

    [Fact]
    public void A_type_change_on_a_filtered_field_renders_the_new_revision_s_filter()
    {
        var recompute = new RollupRecompute(
            new SqliteSqlDialect(),
            new SqliteFieldSqlRenderer(),
            _services.GetRequiredService<ICelCompiler>(),
            _services.GetRequiredService<IPredicateRenderer>());

        recompute.Setter(Reservations(FieldType.Integer), Total).ShouldNotContain("AS REAL");

        recompute.Setter(Reservations(FieldType.Decimal), Total)
            .ShouldContain("CAST(\"quantity\" AS REAL)", Case.Sensitive, "a decimal stored as TEXT compared uncast admits every child");
    }

    private static FieldSchema Total { get; } = new()
    {
        Name = "large_total",
        Type = FieldType.Integer,
        Nullable = true,
        Rollup = new RollupSchema { From = "reservations", Op = RollupOperation.Count, Via = "product_id", Where = "quantity > 1" },
    };

    private static EntitySchema Reservations(FieldType quantity) => new()
    {
        Name = "reservations",
        Tenancy = TenancyMode.Global,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "product_id", Type = FieldType.Ref, Required = true, Reference = new RefSchema("products", OnDelete.Cascade) },
            new FieldSchema
            {
                Name = "quantity", Type = quantity, Required = true,
                Precision = quantity == FieldType.Decimal ? 18 : null, Scale = quantity == FieldType.Decimal ? 2 : null,
            },
        ],
    };
}
