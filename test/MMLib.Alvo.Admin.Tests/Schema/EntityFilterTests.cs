using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>The entity list narrows as the operator types (inventory feature gap #2).</summary>
public sealed class EntityFilterTests
{
    private static readonly string[] _names = ["customers", "regions", "work_orders", "work_order_lines"];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_term_is_every_entity_in_order(string? term)
        => EntityFilter.Apply(_names, term).ShouldBe(_names);

    [Fact]
    public void A_term_keeps_the_names_that_contain_it_ignoring_case_and_the_ends_of_the_term()
        => EntityFilter.Apply(_names, "  WORK ").ShouldBe(["work_orders", "work_order_lines"]);

    [Fact]
    public void A_term_nothing_contains_is_an_empty_list()
        => EntityFilter.Apply(_names, "invoices").ShouldBeEmpty();
}
