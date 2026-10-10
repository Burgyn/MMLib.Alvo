using System.Text;

namespace MMLib.Alvo.DocsGen.Schema;

internal static class DescriptorPageSplit
{
    internal const string EntitiesKey = "entities";

    private static readonly (string Prefix, string Slug)[] _prefixes =
    [
        ("entities.<name>.fields.<name>.computed", "entities-computed-and-rollups"),
        ("entities.<name>.fields.<name>.rollup", "entities-computed-and-rollups"),
        ("entities.<name>.fields", "entities-fields"),
        ("entities.<name>.rules", "entities-rules"),
        ("entities.<name>.hooks", "entities-hooks"),
        ("entities.<name>.indexes", "entities-indexes"),
    ];

    internal static IReadOnlyList<(string Slug, string Label, string Title)> EntityPages { get; } =
    [
        ("entities", "entities", "entities"),
        ("entities-fields", "entities · fields", "entities.fields"),
        ("entities-computed-and-rollups", "entities · computed & rollups", "entities.fields: computed and rollup"),
        ("entities-rules", "entities · rules", "entities.rules"),
        ("entities-hooks", "entities · hooks", "entities.hooks"),
        ("entities-indexes", "entities · indexes", "entities.indexes"),
    ];

    internal static string PageOf(string keyPath)
    {
        foreach (var (prefix, slug) in _prefixes)
        {
            if (HasPrefix(keyPath, prefix))
            {
                return slug;
            }
        }

        return Slug(TopLevelKeyOf(keyPath));
    }

    internal static string Slug(string topLevelKey)
    {
        var builder = new StringBuilder(topLevelKey.Length + 4);
        foreach (var character in topLevelKey.TrimStart('$'))
        {
            if (char.IsUpper(character) && builder.Length > 0)
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string TopLevelKeyOf(string keyPath)
    {
        var end = keyPath.IndexOfAny(['.', '[']);
        return end < 0 ? keyPath : keyPath[..end];
    }

    private static bool HasPrefix(string keyPath, string prefix) =>
        keyPath.StartsWith(prefix, StringComparison.Ordinal)
        && (keyPath.Length == prefix.Length || keyPath[prefix.Length] is '.' or '[');
}
