using System.Text.RegularExpressions;

namespace MMLib.Alvo.Admin.Tests.Internal;

/// <summary>
/// The stylesheet's class names, media queries and stacking, read as data for the hygiene checks.
/// </summary>
internal static partial class Stylesheet
{
    /// <summary>The marker a rule carries when only the gallery draws it.</summary>
    internal const string GalleryOnlyMarker = "gallery-only";

    /// <summary>The folder the product's markup, code and scripts live in.</summary>
    internal static string AdminSourcePath { get; } = Path.Combine(RepositoryRoot.Find(), "src", "MMLib.Alvo.Admin");

    /// <summary>Every <c>a-*</c> class a selector of <paramref name="css"/> names, comments aside.</summary>
    /// <param name="css">The stylesheet's text.</param>
    /// <returns>The class names, without the leading dot.</returns>
    internal static IReadOnlySet<string> DefinedClasses(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        return ClassesIn(Comment().Replace(css, string.Empty));
    }

    /// <summary>The classes named by a rule that a <c>/* gallery-only … */</c> comment opens.</summary>
    /// <param name="css">The stylesheet's text.</param>
    /// <returns>The class names, without the leading dot.</returns>
    internal static IReadOnlySet<string> GalleryOnlyClasses(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        return ClassesIn(string.Join('\n', MarkedRule().Matches(css).Select(match => match.Groups["selector"].Value)));
    }

    /// <summary>
    /// Every <c>a-*</c> class the product's own sources name as a class — in markup, in a string the code builds
    /// a class from, or in a script's selector.
    /// </summary>
    /// <remarks>
    /// A name counts where it opens a token: after a quote, a space, a brace, a backtick or a dot. That keeps
    /// prose such as "applying-a-draft" and a regular expression's <c>[a-z0-9_]</c> out, and admits
    /// <c>"a-tab{(…)}"</c>, <c>" a-tab--active"</c> and <c>'.a-content'</c>.
    /// </remarks>
    /// <returns>The class names.</returns>
    internal static IReadOnlySet<string> ClassesTheProductNames()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in ProductSources())
        {
            names.UnionWith(ProductClass().Matches(File.ReadAllText(file)).Select(match => match.Value));
        }

        return names;
    }

    /// <summary>The widths every <c>@media</c> query of <paramref name="css"/> breaks at, in source order.</summary>
    /// <param name="css">The stylesheet's text.</param>
    /// <returns>Each width as written, such as <c>720px</c>.</returns>
    internal static IReadOnlyList<string> MediaWidths(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        return [.. MediaWidth().Matches(css).Select(match => match.Groups["width"].Value)];
    }

    /// <summary>The value of every <c>z-index</c> declaration in <paramref name="css"/>, in source order.</summary>
    /// <param name="css">The stylesheet's text.</param>
    /// <returns>Each value as written.</returns>
    internal static IReadOnlyList<string> ZIndexValues(string css)
    {
        ArgumentNullException.ThrowIfNull(css);
        return [.. ZIndex().Matches(css).Select(match => match.Groups["value"].Value.Trim())];
    }

    private static HashSet<string> ClassesIn(string text)
        => new(SelectorClass().Matches(text).Select(match => match.Groups["name"].Value), StringComparer.Ordinal);

    private static IEnumerable<string> ProductSources()
        => Directory.EnumerateFiles(AdminSourcePath, "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".razor", StringComparison.Ordinal)
                || file.EndsWith(".cs", StringComparison.Ordinal)
                || file.EndsWith(".js", StringComparison.Ordinal))
            .Where(file => !IsBuildOutput(file));

    private static bool IsBuildOutput(string file)
    {
        var relative = Path.GetRelativePath(AdminSourcePath, file).Replace('\\', '/');
        return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex Comment();

    [GeneratedRegex(@"/\*\s*gallery-only\b.*?\*/\s*(?<selector>[^{}]+)\{", RegexOptions.Singleline)]
    private static partial Regex MarkedRule();

    [GeneratedRegex(@"\.(?<name>a-[a-z0-9]+(?:(?:--|__|-)[a-z0-9]+)*)")]
    private static partial Regex SelectorClass();

    [GeneratedRegex(@"(?<=[""'\s{`.])a-[a-z0-9]+(?:(?:--|__|-)[a-z0-9]+)*")]
    private static partial Regex ProductClass();

    [GeneratedRegex(@"@media[^{]*?\((?:max|min)-width:\s*(?<width>[0-9.]+[a-z]+)\)")]
    private static partial Regex MediaWidth();

    [GeneratedRegex(@"z-index:(?<value>[^;]+);")]
    private static partial Regex ZIndex();
}
