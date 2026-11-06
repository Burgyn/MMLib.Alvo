using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// The apply's rollup backfill keeps the write path's fail-closed belt against a pair that disagrees about tenancy
/// (Task 11 review m2): with only one side scoped the correlated subquery would carry no tenant predicate and aggregate
/// every tenant's children into one parent — the same refusal <see cref="RollupRecompute"/> makes on a write.
/// </summary>
/// <remarks>
/// Asserted on <see cref="RollupBackfill.Targets"/>, before any SQL is composed, because no descriptor can produce the
/// schema: <c>RollupResolver</c> refuses the pair at apply, and only a host-assembled schema reaches here with it.
/// </remarks>
public class RollupBackfillTenancyTests
{
    [Theory]
    [InlineData(TenancyMode.Scoped, TenancyMode.Global)]
    [InlineData(TenancyMode.Global, TenancyMode.Scoped)]
    public void A_backfill_over_a_pair_that_disagrees_about_tenancy_is_refused(TenancyMode parent, TenancyMode child)
    {
        var refused = Should.Throw<InvalidOperationException>(() => RollupBackfill.Targets(Stored(parent, child), Desired(parent, child)));

        refused.Message.ShouldContain("Entity 'invoices' rolls up 'invoice_items'");
    }

    /// <summary>The counterweight: a pair that agrees is a target, so the refusal above is not the only arm.</summary>
    [Theory]
    [InlineData(TenancyMode.Scoped)]
    [InlineData(TenancyMode.Global)]
    public void A_backfill_over_a_pair_that_agrees_is_a_target(TenancyMode tenancy) =>
        RollupBackfill.Targets(Stored(tenancy, tenancy), Desired(tenancy, tenancy)).ShouldHaveSingleItem().Field.Name.ShouldBe("net_total");

    private static SchemaModel Stored(TenancyMode parent, TenancyMode child) => new([Parent(parent, rollup: null), Child(child)]);

    private static SchemaModel Desired(TenancyMode parent, TenancyMode child) => new([Parent(parent, Sum), Child(child)]);

    private static RollupSchema Sum =>
        new() { From = "invoice_items", Via = "invoice", Op = RollupOperation.Sum, Field = "line_total" };

    private static EntitySchema Parent(TenancyMode tenancy, RollupSchema? rollup) => new()
    {
        Name = "invoices",
        Tenancy = tenancy,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "net_total", Type = FieldType.Decimal, Rollup = rollup },
        ],
    };

    private static EntitySchema Child(TenancyMode tenancy) => new()
    {
        Name = "invoice_items",
        Tenancy = tenancy,
        Fields =
        [
            new FieldSchema { Name = "id", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "invoice", Type = FieldType.Uuid, Required = true },
            new FieldSchema { Name = "line_total", Type = FieldType.Decimal },
        ],
    };
}
