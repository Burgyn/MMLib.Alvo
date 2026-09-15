using MMLib.Alvo.Schema;
using MMLib.Alvo.Testing.Data;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The belt <see cref="RollupRecompute"/> keeps against a schema that did not come through the descriptor
/// mapper — the same class of schema <c>EnsureTenancyDoesNotCross</c> exists for.
/// </summary>
/// <remarks>
/// A host may assemble a <see cref="SchemaModel"/> programmatically, and F7's dynamic registry produces one
/// without a descriptor at all; neither goes through <c>RollupResolver</c>, whose
/// <c>EnsureAggregatedFieldIsResolvable</c> is what normally refuses this pair at apply. The statement is
/// asserted rather than the effect because the effect is unreachable: no descriptor can produce the schema.
/// </remarks>
public class RollupAggregateStatementTests
{
    /// <summary>
    /// The refusal names the entity, the field and what the child does declare, because the caller sees it
    /// from inside a write transaction and has nothing else to go on.
    /// </summary>
    /// <remarks>
    /// It used to be <c>First</c>, whose <c>Sequence contains no matching element</c> names none of the three.
    /// <see cref="Expressions.IFieldSqlRenderer.RenderField"/> runs first on the same field and does not catch
    /// it: it quotes whatever name it is handed and never consults the schema.
    /// </remarks>
    [Fact]
    public void An_aggregate_over_a_field_the_child_does_not_declare_is_refused()
    {
        var refused = Should.Throw<InvalidOperationException>(() => Setter(Sum("total_owed")));

        refused.Message.ShouldContain("aggregates 'invoice_items.total_owed', which 'invoice_items' does not declare");
        refused.Message.ShouldContain("Declared fields on 'invoice_items': id, invoice, line_total");
    }

    /// <summary>A field the child does declare composes the subquery, so the refusal above is not the only arm.</summary>
    [Fact]
    public void An_aggregate_over_a_declared_field_composes_the_subquery()
        => Setter(Sum("line_total")).ShouldContain("SUM(");

    /// <summary><c>count</c> aggregates rows and names no column, so it never reaches the lookup at all.</summary>
    [Fact]
    public void A_count_needs_no_declared_field()
        => Setter(new RollupSchema { From = "invoice_items", Via = "invoice", Op = RollupOperation.Count })
            .ShouldContain("COUNT(*)");

    /// <summary>
    /// The subquery follows the rollup's own foreign key, and is closed — the parent id is bound into the
    /// child's <c>WHERE</c> rather than the aggregate being taken over the whole child table.
    /// </summary>
    [Fact]
    public void An_aggregate_is_narrowed_to_the_children_of_one_parent()
        => Setter(Sum("line_total")).ShouldEndWith("""FROM "invoice_items" WHERE "invoice" = {0})""");

    /// <summary>
    /// Every value operation composes <b>its own</b> SQL aggregate, because a defaulted one is a wrong number
    /// nothing reports: the statement still runs, the parent still stores something, and no reader can tell it
    /// from a right one.
    /// </summary>
    /// <remarks>
    /// All four are asserted rather than a representative one: the mapping is a table, and a table goes wrong
    /// one row at a time. <c>count</c> never reaches it — it aggregates rows and short-circuits to
    /// <c>COUNT(*)</c> above.
    /// </remarks>
    [Theory]
    [InlineData(RollupOperation.Sum, "SUM")]
    [InlineData(RollupOperation.Avg, "AVG")]
    [InlineData(RollupOperation.Min, "MIN")]
    [InlineData(RollupOperation.Max, "MAX")]
    public void Each_value_operation_composes_its_own_sql_aggregate(RollupOperation op, string aggregate)
        => Setter(Aggregate(op, "line_total")).ShouldContain($"SELECT {aggregate}(");

    /// <summary>
    /// An operation with no aggregate mapped is <b>refused</b> rather than defaulted, and the refusal carries
    /// the unmapped value — the enum is appended to, and a rollup silently aggregated as something else is the
    /// same stored-wrong-number failure the whole feature exists to remove.
    /// </summary>
    [Fact]
    public void An_operation_with_no_aggregate_mapped_is_refused()
    {
        var refused = Should.Throw<ArgumentOutOfRangeException>(() => Setter(Aggregate(Unmapped, "line_total")));

        refused.ParamName.ShouldBe("op");
        refused.ActualValue.ShouldBe(Unmapped);
    }

    /// <summary>A member no <c>switch</c> arm names — the shape a later append to the enum has here.</summary>
    private static RollupOperation Unmapped => (RollupOperation)99;

    private static string Setter(RollupSchema rollup) =>
        new RollupRecompute(new TestSqlDialect(), new TestFieldSqlRenderer())
            .Setter(Child, new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Rollup = rollup });

    private static RollupSchema Sum(string field) => Aggregate(RollupOperation.Sum, field);

    private static RollupSchema Aggregate(RollupOperation op, string field) =>
        new() { From = "invoice_items", Via = "invoice", Op = op, Field = field };

    private static EntitySchema Child => new()
    {
        Name = "invoice_items",
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "invoice", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "line_total", Type = FieldType.Decimal },
        ],
    };
}
