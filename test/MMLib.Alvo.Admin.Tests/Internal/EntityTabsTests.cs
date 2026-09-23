using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The entity screen's tab, read from and written to the address, and moved by the arrow keys.
/// </summary>
public class EntityTabsTests
{
    [Theory]
    [InlineData("https://host/admin/schema/work_orders?tab=rules", "Rules")]
    [InlineData("https://host/admin/schema/work_orders?tab=ON-WRITE", "On write")]
    [InlineData("https://host/admin/schema/work_orders?other=1&tab=api", "API")]
    [InlineData("https://host/admin/schema/work_orders", "Fields")]
    [InlineData("https://host/admin/schema/work_orders?tab=renamed-long-ago", "Fields")]
    [InlineData("https://host/admin/schema/work_orders?tab=", "Fields")]
    public void The_address_names_the_tab_or_the_screen_opens_on_fields(string uri, string title)
        => EntityTabs.FromUri(uri).Title.ShouldBe(title);

    [Fact]
    public void Every_slug_is_lower_case_distinct_and_reads_back_as_its_own_tab()
    {
        EntityTabs.All.Select(tab => tab.Slug).ShouldBeUnique();

        foreach (var tab in EntityTabs.All)
        {
            tab.Slug.ShouldBe(tab.Slug.ToLowerInvariant());
            EntityTabs.FromSlug(tab.Slug).ShouldBe(tab);
        }
    }

    [Theory]
    [InlineData("fields", "ArrowRight", "relationships")]
    [InlineData("api", "ArrowRight", "fields")]
    [InlineData("fields", "ArrowLeft", "api")]
    [InlineData("rules", "ArrowLeft", "relationships")]
    [InlineData("rules", "Home", "fields")]
    [InlineData("rules", "End", "api")]
    public void The_arrows_wrap_and_home_and_end_reach_the_ends(string from, string key, string to)
        => EntityTabs.Move(EntityTabs.FromSlug(from), key).ShouldBe(EntityTabs.FromSlug(to));

    [Theory]
    [InlineData("Enter")]
    [InlineData("ArrowDown")]
    [InlineData("a")]
    public void Any_other_key_moves_nothing(string key)
        => EntityTabs.Move(EntityTabs.First, key).ShouldBeNull();
}
