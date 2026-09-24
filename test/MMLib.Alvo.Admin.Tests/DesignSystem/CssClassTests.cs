using MMLib.Alvo.Admin.Components.DesignSystem;

namespace MMLib.Alvo.Admin.Tests.DesignSystem;

/// <summary>
/// The class a primitive renders keeps its own block whatever class the page adds.
/// </summary>
public class CssClassTests
{
    [Fact]
    public void With_nothing_added_the_block_is_the_whole_class()
        => CssClass.Of("a-panel", null).ShouldBe("a-panel");

    [Fact]
    public void A_modifier_that_is_set_follows_the_block_and_one_that_is_not_is_left_out()
        => CssClass.Of("a-panel", null, "a-panel--padded", null).ShouldBe("a-panel a-panel--padded");

    /// <summary>
    /// The failure this exists for: splatted on its own, a page's <c>class</c> replaces the block class, and
    /// the panel renders as a bare div that still passes every test that only looks for its text.
    /// </summary>
    [Fact]
    public void A_class_the_page_adds_is_appended_rather_than_replacing_the_block()
    {
        var attributes = new Dictionary<string, object> { ["class"] = " a-panel--accent ", ["id"] = "token" };

        CssClass.Of("a-panel", attributes, "a-panel--padded").ShouldBe("a-panel a-panel--padded a-panel--accent");
    }

    [Fact]
    public void Attributes_without_a_class_add_nothing()
        => CssClass.Of("a-listrow", new Dictionary<string, object> { ["data-testid"] = "plan-step" })
            .ShouldBe("a-listrow");
}
