using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql.Tests;

/// <summary>
/// A <c>rollup.where</c> rendered by the core's own compiler and renderer into PostgreSQL's recompute (HX-11 Ruling 31): ANDed
/// into the aggregate's subquery, its columns unqualified, and its text passed through <see cref="RawSqlFormat.Escape"/>
/// because EF formats the statement with its arguments.
/// </summary>
public sealed class RollupRecomputeFilterTests
{
    [Fact]
    public void The_filter_is_anded_into_the_aggregate_s_subquery() =>
        RollupFixtures.Recompute().Setter(RollupFixtures.Lines, RollupFixtures.HeldTotal)
            .ShouldBe("\"held_total\" = (SELECT SUM(\"quantity\") FROM \"reservations\" WHERE \"product_id\" = {0} AND (COALESCE(\"status\" = E'held', FALSE)))");

    [Fact]
    public void A_filter_constant_with_braces_is_written_doubled_and_matched_literally() =>
        RollupFixtures.Recompute().Setter(RollupFixtures.Lines, RollupFixtures.Filtered("label == '{0}'"))
            .ShouldEndWith("AND (COALESCE(\"label\" = E'{{0}}', FALSE)))");

    [Fact]
    public void Escape_doubles_both_braces_and_nothing_else() =>
        RawSqlFormat.Escape("a{b}c'{{").ShouldBe("a{{b}}c'{{{{");

    [Fact]
    public void A_rollup_with_no_filter_aggregates_every_child() =>
        RollupFixtures.Recompute().Setter(RollupFixtures.Lines, RollupFixtures.Filtered(where: null))
            .ShouldBe("\"held_total\" = (SELECT SUM(\"quantity\") FROM \"reservations\" WHERE \"product_id\" = {0})");

    /// <summary>
    /// The recompute over PostgreSQL's dialect and field renderer and the <see cref="ICelCompiler"/>/<see cref="IPredicateRenderer"/>
    /// <c>AddAlvo()</c> registers, with <c>reservations</c> (<c>product_id</c>, <c>quantity</c>, <c>status</c>, <c>label</c>) and
    /// <c>products.held_total</c> (<c>sum</c> of <c>quantity</c> where <c>status == 'held'</c>).
    /// </summary>
    private static class RollupFixtures
    {
        private static readonly ServiceProvider _services = new ServiceCollection().AddAlvo().Services.BuildServiceProvider();

        internal static RollupRecompute Recompute() => new(
            new PostgreSqlSqlDialect(),
            new PostgreSqlFieldSqlRenderer(),
            _services.GetRequiredService<ICelCompiler>(),
            _services.GetRequiredService<IPredicateRenderer>());

        internal static EntitySchema Lines { get; } = new()
        {
            Name = "reservations",
            Tenancy = TenancyMode.Global,
            Fields =
            [
                new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
                new FieldSchema { Name = "product_id", Type = FieldType.Ref, Required = true, Reference = new RefSchema("products", OnDelete.Cascade) },
                new FieldSchema { Name = "quantity", Type = FieldType.Decimal, Required = true, Precision = 18, Scale = 2 },
                new FieldSchema { Name = "status", Type = FieldType.Enum, EnumValues = ["held", "released"], Nullable = true },
                new FieldSchema { Name = "label", Type = FieldType.String, Nullable = true },
            ],
        };

        internal static FieldSchema HeldTotal { get; } = Filtered("status == 'held'");

        internal static FieldSchema Filtered(string? where) => new()
        {
            Name = "held_total",
            Type = FieldType.Decimal,
            Precision = 18,
            Scale = 2,
            Nullable = true,
            Rollup = new RollupSchema { From = "reservations", Op = RollupOperation.Sum, Field = "quantity", Via = "product_id", Where = where },
        };
    }
}
