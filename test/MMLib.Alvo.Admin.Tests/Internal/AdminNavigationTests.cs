using MMLib.Alvo.Admin.Components.Layout;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// Which entry the navigation marks as current, and what the phone's bottom bar calls each one.
/// </summary>
public class AdminNavigationTests
{
    private const string Host = "https://alvo.test";

    [Theory]
    [InlineData("/admin/changes")]
    [InlineData("/admin/transfer")]
    [InlineData("/admin/changes?tab=plan")]
    [InlineData("/host/admin/transfer/")]
    public void Schema_stays_current_on_the_schema_screens_with_addresses_of_their_own(string path)
        => Section("Schema").OwnsAlso(Host + path).ShouldBeTrue();

    [Theory]
    [InlineData("/admin/data")]
    [InlineData("/admin/changeset")]
    [InlineData("/admin/history")]
    public void Schema_claims_no_other_screen(string path)
        => Section("Schema").OwnsAlso(Host + path).ShouldBeFalse();

    [Fact]
    public void Only_schema_claims_screens_beyond_its_own_path()
        => AdminNavigation.All.Where(section => section.AlsoActiveUnder.Count > 0)
            .Select(section => section.Title).ShouldBe(["Schema"]);

    [Fact]
    public void The_bar_reads_a_short_title_only_where_the_title_would_wrap()
        => AdminNavigation.Live.Where(section => section.ShortTitle != section.Title)
            .Select(section => (section.Title, section.ShortTitle))
            .ShouldBe([("Configuration history", "History")]);

    [Theory]
    [InlineData("/admin/changes", true)]
    [InlineData("/admin/changes/", true)]
    [InlineData("/admin/changes/anything", true)]
    [InlineData("/ADMIN/CHANGES", true)]
    [InlineData("/admin/changesx", false)]
    [InlineData("/admin/schema/changes-log", false)]
    public void An_address_is_under_a_path_by_whole_segments(string path, bool under)
        => AdminPaths.IsUnder(Host + path, AdminPaths.Changes).ShouldBe(under);

    private static AdminSection Section(string title) => AdminNavigation.All.Single(section => section.Title == title);
}
