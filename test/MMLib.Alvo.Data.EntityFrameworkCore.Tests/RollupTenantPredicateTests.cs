using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// Both statements of a rollup recompute, and the apply's backfill statement, are narrowed by <c>tenant_id</c> when the
/// pair is scoped, and carry no tenant predicate at all when it is global.
/// </summary>
/// <remarks>
/// <para>
/// <b>The predicate is defence in depth, and these facts are its only pin.</b> Since #161 a <c>ref</c> between two scoped
/// entities is a key over <c>(tenant_id, id)</c>, so a child row can no longer name a parent in another tenant and the
/// shared suite's two-tenant facts pass with or without the predicate. It stays because an id taken off a row image is
/// not a promise about whose row it is, and a <see cref="SchemaModel"/> that never came through the mapper has no such
/// key: without it the recompute would write that other tenant's parent row from this tenant's children, and the
/// backfill would aggregate every tenant's children into it — a cross-tenant write and a cross-tenant read.
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

    /// <summary>
    /// A scoped pair's backfill narrows the aliased child by <b>its own</b> <c>tenant_id</c>, correlated to the parent's —
    /// once in the <c>SET</c> and once in the "differs" test, because both carry the same subquery.
    /// </summary>
    [Fact]
    public void A_scoped_pairs_backfill_is_narrowed_to_the_parents_tenant()
        => Occurrences(Backfill(TenancyMode.Scoped), BackfillTenantPredicate).ShouldBe(2);

    /// <summary>A global pair's backfill names no tenant column anywhere.</summary>
    [Fact]
    public void A_global_pairs_backfill_carries_no_tenant_predicate()
        => Backfill(TenancyMode.Global).ShouldNotContain("tenant_id");

    private const string BackfillTenantPredicate =
        """WHERE "alvo_backfill_child"."invoice" = "invoices"."id" AND "alvo_backfill_child"."tenant_id" = "invoices"."tenant_id")""";

    private static int Occurrences(string text, string fragment) => text.Split(fragment).Length - 1;

    private static string Backfill(TenancyMode tenancy)
    {
        var parent = Parent with { Tenancy = tenancy };
        var child = Child with { Tenancy = tenancy };
        var withRollup = parent with
        {
            Fields = [.. parent.Fields, new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Rollup = Sum }],
        };

        return RollupBackfill.Statements(
                new SchemaModel([parent, child]), new SchemaModel([withRollup, child]), new TestSqlDialect(), new TestFieldSqlRenderer(),
                (_, _) => null)
            .ShouldHaveSingleItem().Sql;
    }

    private static string Lock(EntitySchema parent) =>
        Recompute().LockStatement(parent).ShouldNotBeNull();

    private static string Setter(EntitySchema child) =>
        Recompute().Setter(child, new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Rollup = Sum });

    private static RollupRecompute Recompute() => RollupRecomputes.For(new TestSqlDialect(), new TestFieldSqlRenderer());

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
