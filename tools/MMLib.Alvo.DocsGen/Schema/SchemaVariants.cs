using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal static class SchemaVariants
{
    internal const int Direct = -1;

    internal static string? NoteFor(string name, IReadOnlyList<int> sources, IReadOnlyList<SchemaNode> branches, JsonObject root)
    {
        if (branches.Count < 2 || sources.Contains(Direct) || sources.Distinct().Count() == branches.Count)
        {
            return null;
        }

        if (Discriminator(branches, root) is { } discriminator)
        {
            var values = sources.Distinct().Select(index => Md.Code(ConstOf(branches[index], discriminator, root)!)).ToList();
            return $"Only when {Md.Code(discriminator)} is {JoinOr(values)}.";
        }

        return branches.All(branch => PropertyNames(branch).Count == 1)
            ? $"Exactly one of {string.Join(", ", branches.Select(branch => Md.Code(PropertyNames(branch)[0])))}."
            : $"Only in some variants of this object; {Md.Code(name)} is not accepted by the others.";
    }

    private static string? Discriminator(IReadOnlyList<SchemaNode> branches, JsonObject root) =>
        PropertyNames(branches[0]).FirstOrDefault(property => branches.All(branch => ConstOf(branch, property, root) is not null));

    private static string? ConstOf(SchemaNode branch, string property, JsonObject root) =>
        SchemaNode.Resolve(branch.FindObject("properties")?[property], root)?.Find("const") is { } value ? SchemaNode.Literal(value) : null;

    private static List<string> PropertyNames(SchemaNode branch) =>
        [.. (branch.FindObject("properties") ?? []).Where(property => property.Value is JsonObject).Select(property => property.Key)];

    private static string JoinOr(List<string> values) =>
        values.Count == 1 ? values[0] : $"{string.Join(", ", values[..^1])} or {values[^1]}";
}
