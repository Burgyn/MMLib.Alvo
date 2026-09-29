using MMLib.Alvo.Admin.Components.Schema;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The entities a rollup may aggregate, read from the working copy, with the ones the apply refuses said
/// (<c>RollupResolver</c>: a child that references the parent, is physical, and agrees about tenancy).
/// </summary>
public class RollupSourcesTests
{
    [Fact]
    public void Every_entity_with_a_ref_here_is_a_source_with_its_refs_and_number_fields()
    {
        var sources = RollupSources.For(Descriptor, "customers");

        sources.Select(source => source.Entity).ShouldBe(["orders", "follows", "drafts"]);
        var orders = sources[0];
        orders.Via.ShouldBe(["customer_id"]);
        orders.Numbers.Select(field => field.Name).ShouldBe(["priority", "total"]);
        orders.Numbers[1].ShouldBe(new RollupChildField("total", FieldType.Decimal, 10, 2));
        orders.Refusal.ShouldBeNull();
        sources[1].Via.ShouldBe(["follower", "followee"]);
    }

    [Fact]
    public void A_dynamic_child_is_listed_as_refused()
        => RollupSources.For(Descriptor, "customers").Single(source => source.Entity == "drafts").Refusal
            .ShouldNotBeNull().ShouldContain("dynamic");

    /// <summary><c>regions</c> is global and <c>orders</c> resolves scoped (<c>tenancy.enabled</c>), so the pair crosses.</summary>
    [Fact]
    public void A_child_whose_tenancy_disagrees_is_listed_as_refused()
        => RollupSources.For(Descriptor, "regions").Single().Refusal.ShouldNotBeNull().ShouldContain("tenancy");

    [Theory]
    [InlineData("orders")]
    [InlineData("missing")]
    public void An_entity_nothing_points_at_has_no_source(string parent)
        => RollupSources.For(Descriptor, parent).ShouldBeEmpty();

    [Fact]
    public void A_document_that_is_not_json_has_no_source()
        => RollupSources.For("{not json", "customers").ShouldBeEmpty();

    private const string Descriptor = """
        {
          "tenancy": { "enabled": true },
          "entities": {
            "customers": { "fields": { "name": { "type": "string" } } },
            "regions": { "tenancy": "global", "fields": { "code": { "type": "string" } } },
            "orders": { "fields": {
              "customer_id": { "type": "ref", "entity": "customers" },
              "region_id": { "type": "ref", "entity": "regions" },
              "priority": { "type": "integer" },
              "total": { "type": "decimal", "precision": 10, "scale": 2 },
              "note": { "type": "string" } } },
            "follows": { "fields": {
              "follower": { "type": "ref", "entity": "customers" },
              "followee": { "type": "ref", "entity": "customers" } } },
            "drafts": { "storage": "dynamic", "fields": { "customer_id": { "type": "ref", "entity": "customers" } } }
          }
        }
        """;
}
