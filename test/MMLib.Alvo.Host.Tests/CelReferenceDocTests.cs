using MMLib.Alvo.Expressions.Internal;
using System.Text.RegularExpressions;

namespace MMLib.Alvo.Host.Tests;

/// <summary>cel.md's built-in reference names exactly the catalog's built-ins (spec E15).</summary>
public sealed partial class CelReferenceDocTests
{
    [Fact]
    public void The_reference_table_lists_every_built_in_once()
    {
        var doc = File.ReadAllText(Path.Combine(RepositoryRoot.Find(), "docs", "architecture", "cel.md")).ReplaceLineEndings("\n");
        var start = doc.IndexOf("#### Built-in functions", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "cel.md has no '#### Built-in functions' section");
        var section = doc[start..];
        section = section[..section.IndexOf("\n#### ", 5, StringComparison.Ordinal)];

        var named = Row().Matches(section).Select(match => match.Groups["name"].Value).ToList();

        named.ShouldBe(CelFunctionCatalog.BuiltIns.Names);
    }

    [GeneratedRegex(@"^\| `(?<name>[a-zA-Z.]+)` \|", RegexOptions.Multiline)]
    private static partial Regex Row();
}
