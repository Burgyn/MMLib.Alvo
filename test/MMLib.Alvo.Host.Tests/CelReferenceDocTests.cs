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

        var rows = Row().Matches(section).ToList();
        var named = rows.Select(match => match.Groups["name"].Value).ToList();

        named.ShouldBe(CelFunctionCatalog.BuiltIns.Names);
        rows.Select(match => $"{match.Groups["name"].Value}: {Signature().Count(match.Groups["signatures"].Value)}")
            .ShouldBe(named.Select(name => $"{name}: {CelFunctionCatalog.BuiltIns.Overloads(name).Count}"),
                "each row lists one backticked signature per overload the catalog holds");
    }

    [GeneratedRegex(@"^\| `(?<name>[a-zA-Z.]+)` \| (?<signatures>[^|]*) \|", RegexOptions.Multiline)]
    private static partial Regex Row();

    [GeneratedRegex(@"`\(")]
    private static partial Regex Signature();
}
