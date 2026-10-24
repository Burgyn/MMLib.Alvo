using MMLib.Alvo.Admin.Components.Schema;

namespace MMLib.Alvo.Admin.Tests.Schema;

/// <summary>
/// The entity screen's tab, read from and written to the address.
/// </summary>
public class EntityTabsTests
{
    [Theory]
    [InlineData("https://host/admin/schema/work_orders?tab=rules", "Rules")]
    [InlineData("https://host/admin/schema/work_orders?tab=ON-WRITE", "On write")]
    [InlineData("https://host/admin/schema/work_orders?other=1&tab=api", "API")]
    [InlineData("https://host/admin/schema/work_orders?tab=lifecycle", "Lifecycle")]
    [InlineData("https://host/admin/schema/work_orders", "Fields")]
    [InlineData("https://host/admin/schema/work_orders?tab=renamed-long-ago", "Fields")]
    [InlineData("https://host/admin/schema/work_orders?tab=", "Fields")]
    public void The_address_names_the_tab_or_the_screen_opens_on_fields(string uri, string title)
        => EntityTabs.FromUri(uri).Title.ShouldBe(title);

    [Fact]
    public void The_lifecycle_tab_follows_on_write()
    {
        var slugs = EntityTabs.All.Select(tab => tab.Slug).ToList();

        slugs.IndexOf(EntityTabs.Lifecycle).ShouldBe(slugs.IndexOf(EntityTabs.OnWrite) + 1);
    }

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
}
