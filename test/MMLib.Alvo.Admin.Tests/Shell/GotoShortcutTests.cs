using MMLib.Alvo.Admin.Components.Shell;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.Shell;

/// <summary>
/// <c>g</c> then a letter: the letters design §5.5 promises, read from the navigation itself.
/// </summary>
public class GotoShortcutTests
{
    [Theory]
    [InlineData("o", "Overview")]
    [InlineData("s", "Schema")]
    [InlineData("d", "Data")]
    [InlineData("r", "Rules")]
    [InlineData("a", "Access")]
    [InlineData("h", "Configuration history")]
    [InlineData("i", "Integrations")]
    [InlineData(",", "Settings")]
    public void Each_letter_reaches_its_section(string key, string title)
        => AdminNavigation.GoTo(key)!.Title.ShouldBe(title);

    [Theory]
    [InlineData("x")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("ds")]
    public void Anything_else_reaches_nothing(string? key) => AdminNavigation.GoTo(key).ShouldBeNull();

    [Fact]
    public void No_two_sections_share_a_letter_and_a_section_not_yet_built_has_none()
    {
        AdminNavigation.All.Where(section => section.GoKey is not null)
            .Select(section => section.GoKey).ShouldBeUnique();
        AdminNavigation.NotYet.ShouldAllBe(section => section.GoKey == null);
    }

    [Fact]
    public void The_palette_spells_the_shortcut_beside_the_section()
    {
        AdminNavigation.Hint(AdminNavigation.GoTo("d")!).ShouldBe("g d");
        AdminNavigation.Hint(AdminNavigation.NotYet[0]).ShouldBeNull();
    }

    /// <summary>
    /// alvo.js decides which keys may follow <c>g</c> without asking .NET; a letter the navigation
    /// takes that the script's pattern refuses is a shortcut the palette advertises and nothing honours.
    /// </summary>
    [Fact]
    public void Every_letter_the_navigation_takes_is_one_alvo_js_forwards()
    {
        var script = File.ReadAllText(Path.Combine(
            RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin", "wwwroot", "alvo.js"));
        var pattern = Regex.Match(script, "GOTO_KEY = /(.+?)/;").Groups[1].Value;

        pattern.ShouldNotBeEmpty();
        foreach (var section in AdminNavigation.All.Where(section => section.GoKey is not null))
        {
            Regex.IsMatch(section.GoKey!.Value.ToString(), pattern)
                .ShouldBeTrue($"alvo.js does not forward g {section.GoKey}");
        }
    }

    [Theory]
    [InlineData("https://host/admin/schema?new=entity", true)]
    [InlineData("https://host/admin/schema/?other=1&new=entity", true)]
    [InlineData("https://host/admin/schema", false)]
    [InlineData("https://host/admin/schema?new=field", false)]
    [InlineData("https://host/admin/schema/work_orders?new=entity", false)]
    [InlineData("https://host/admin/data?next=/admin/schema?new=entity", false)]
    public void Schema_opens_its_new_entity_form_only_when_its_own_address_asks(string uri, bool asks)
        => AdminNavigation.IsNewEntity(uri).ShouldBe(asks);
}
