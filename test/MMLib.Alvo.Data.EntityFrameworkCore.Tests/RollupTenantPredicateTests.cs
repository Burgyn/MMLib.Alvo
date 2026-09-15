using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Both statements of a rollup recompute are narrowed by <c>tenant_id</c> when the pair is scoped, and carry
/// no tenant predicate at all when it is global.
/// </summary>
/// <remarks>
/// <para>
/// <b>The predicate is not an optimisation, it is the tenant boundary.</b> The physical <c>ref</c> is a foreign
/// key on the parent's <c>id</c> alone, so a child row may legally name a parent in another tenant and an id
/// taken off a row image is not a promise about whose row it is. Without the predicate the recompute writes
/// that other tenant's parent row from this tenant's children, and aggregates every tenant's children into it —
/// a cross-tenant write and a cross-tenant read in one statement pair.
/// </para>
/// <para>
/// Asserted on the composed text rather than through an engine, because the two halves differ in a way only the
/// text shows: the parent's predicate is unqualified (its statement has one table source) while the child's is
/// qualified by the child's own table, since a bare <c>tenant_id</c> inside the subquery binds to the
/// <em>parent's</em> column the moment the child has none — a predicate that is true for every child row, with
/// no error anywhere.
/// </para>
/// </remarks>
public class RollupTenantPredicateTests
{
    /// <summary>A scoped parent's row lock takes the tenant as well as the id.</summary>
    [Fact]
    public void A_scoped_parents_lock_is_narrowed_to_the_tenant()
        => Lock(Scoped(Parent)).ShouldBe(
            """SELECT "id" FROM "invoices" WHERE "id" = {0} AND "tenant_id" = {1} FOR TEST""");

    /// <summary>A global parent's carries no tenant predicate, and binds no second argument to match.</summary>
    [Fact]
    public void A_global_parents_lock_carries_no_tenant_predicate()
        => Lock(Parent).ShouldBe("""SELECT "id" FROM "invoices" WHERE "id" = {0} FOR TEST""");

    /// <summary>
    /// A scoped child's aggregate is narrowed by <b>the child's own</b> <c>tenant_id</c>, qualified by the
    /// child's table — the unqualified spelling binds to the parent's column and fails open.
    /// </summary>
    [Fact]
    public void A_scoped_childs_aggregate_is_narrowed_by_the_childs_own_table()
        => Setter(Scoped(Child)).ShouldEndWith(
            """WHERE "invoice" = {0} AND "invoice_items"."tenant_id" = {1})""");

    /// <summary>A global child's aggregate ends at the foreign key, with nothing appended after it.</summary>
    [Fact]
    public void A_global_childs_aggregate_carries_no_tenant_predicate()
        => Setter(Child).ShouldEndWith("""WHERE "invoice" = {0})""");

    private static string Lock(EntitySchema parent) =>
        Recompute().LockStatement(parent).ShouldNotBeNull();

    private static string Setter(EntitySchema child) =>
        Recompute().Setter(child, new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Rollup = Sum });

    private static RollupRecompute Recompute() => new(new TestSqlDialect(), new TestFieldSqlRenderer());

    private static RollupSchema Sum =>
        new() { From = "invoice_items", Via = "invoice", Op = RollupOperation.Sum, Field = "line_total" };

    private static EntitySchema Scoped(EntitySchema entity) => entity with { Tenancy = TenancyMode.Scoped };

    private static EntitySchema Parent => new()
    {
        Name = "invoices",
        Fields = [new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true }],
    };

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
