using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal sealed record VariantGroup(string Condition, string Label, bool Required, string Description, IReadOnlyList<SchemaNode> Schemas);

internal static class SchemaVariants
{
    internal const int Direct = -1;

    internal static bool IsPartial(SchemaMember member, IReadOnlyList<SchemaNode> branches) =>
        branches.Count >= 2 && member.IsVariantMember && member.Sources.Distinct().Count() < branches.Count;

    internal static string? NoteFor(SchemaMember member, IReadOnlyList<SchemaNode> branches, JsonObject root)
    {
        if (!IsPartial(member, branches))
        {
            return null;
        }

        if (Discriminator(branches, root) is not null)
        {
            return Condition(member.Sources, branches, root, "Only when") + ".";
        }

        return branches.All(branch => PropertyNames(branch).Count == 1)
            ? $"Exactly one of {string.Join(", ", branches.Select(branch => Md.Code(PropertyNames(branch)[0])))}."
            : $"Only in some variants of this object; {Md.Code(member.Name)} is not accepted by the others.";
    }

    internal static IReadOnlyList<VariantGroup>? Groups(SchemaMember member, IReadOnlyList<SchemaNode> branches, JsonObject root)
    {
        if (branches.Count < 2 || !member.IsVariantMember)
        {
            return null;
        }

        var facts = member.Schemas.Select((schema, index) => (Source: member.Sources[index], Schema: schema, Required: member.RequiredFlags[index], Label: schema.TypeLabel(root))).ToList();
        var agree = facts.Select(fact => fact.Label).Distinct(StringComparer.Ordinal).Count() == 1
            && facts.Select(fact => fact.Required).Distinct().Count() == 1
            && facts.Select(fact => fact.Schema.Description).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).Count() <= 1;
        if (agree)
        {
            return null;
        }

        return [.. facts
            .GroupBy(fact => (fact.Label, fact.Required, fact.Schema.Description))
            .Select(group => new VariantGroup(
                Condition([.. group.Select(fact => fact.Source)], branches, root),
                group.Key.Label,
                group.Key.Required,
                group.Key.Description,
                [.. group.Select(fact => fact.Schema)]))];
    }

    private static string Condition(IReadOnlyList<int> sources, IReadOnlyList<SchemaNode> branches, JsonObject root, string lead = "When")
    {
        var distinct = sources.Distinct().ToList();
        if (Discriminator(branches, root) is { } discriminator)
        {
            return $"{lead} {Md.Code(discriminator)} is {JoinOr([.. distinct.Select(index => Md.Code(ConstOf(branches[index], discriminator, root)!))])}";
        }

        return $"In the alternative with {JoinOr([.. distinct.Select(index => Md.Code(PropertyNames(branches[index]).FirstOrDefault() ?? "?"))])}";
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
