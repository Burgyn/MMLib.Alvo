using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaWalker(JsonObject root)
{
    private const string EntryPlaceholder = ".<name>";
    private const string ItemPlaceholder = "[]";
    private const string VariesDescription = "Depends on the variant; see the notes below.";
    private const string SelectsVariantDescription = "Selects the variant.";
    private const string RequiredVaries = "depends on the variant";
    private const string RequiredInVariant = "yes (in its variant)";

    private readonly SchemaNode _root = SchemaNode.Resolve(root, root)!;
    private readonly List<string> _unrecognised = [];
    private readonly List<SchemaKey> _keys = [];
    private readonly HashSet<string> _emitted = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Page, string Definition), (string Path, string Anchor)> _expanded = [];

    internal IReadOnlyList<string> TopLevelKeys => [.. (_root.FindObject("properties") ?? []).Select(property => property.Key)];

    internal IReadOnlyList<string> UnrecognisedConditions => _unrecognised;

    internal IReadOnlyList<string> RootNotes =>
    [
        .. (_root.FindObject("patternProperties") ?? []).Select(pattern => PatternNote(pattern.Key, pattern.Value)),
    ];

    internal string Description => _root.Description;

    internal bool IsBlock(string key) =>
        TopLevel(key) is { } node
        && (node.HasProperties || node.FindObject("additionalProperties") is not null || node.ObjectBranches(root).Count > 0);

    internal IReadOnlyList<SchemaKey> KeysOf(string topLevelKey)
    {
        _keys.Clear();
        _emitted.Clear();
        _expanded.Clear();
        var node = TopLevel(topLevelKey) ?? throw new ArgumentException($"'{topLevelKey}' is not a top-level key.", nameof(topLevelKey));
        Visit(topLevelKey, topLevelKey, SchemaMember.Direct(topLevelKey, node, _root.FindStrings("required").Contains(topLevelKey)), [], []);
        return [.. _keys];
    }

    private SchemaNode? TopLevel(string key) => SchemaNode.Resolve(_root.FindObject("properties")?[key], root);

    private void Visit(string path, string anchor, SchemaMember member, IReadOnlyList<SchemaNode> branches, IReadOnlyList<string> leadingNotes)
    {
        if (!_emitted.Add(path))
        {
            return;
        }

        var groups = SchemaVariants.Groups(member, branches, root);
        var notes = new List<string>(leadingNotes);
        _keys.Add(CreateKey(path, anchor, member, branches, groups, notes));
        if (groups is null)
        {
            ExpandAgreeing(path, anchor, member, notes);
        }
        else
        {
            ExpandPerVariant(path, anchor, groups, notes);
        }
    }

    private void ExpandAgreeing(string path, string anchor, SchemaMember member, List<string> notes)
    {
        notes.AddRange(member.Schemas.SelectMany(StructureNotes));
        foreach (var schema in member.Schemas)
        {
            Expand(path, anchor, schema, notes, "");
        }
    }

    private void ExpandPerVariant(string path, string anchor, IReadOnlyList<VariantGroup> groups, List<string> notes)
    {
        foreach (var group in groups)
        {
            notes.Add(VariantNote(group));
            foreach (var schema in group.Schemas)
            {
                Expand(path, anchor, schema, notes, group.Condition + ": ");
            }
        }
    }

    private string VariantNote(VariantGroup group)
    {
        var note = $"{group.Condition}: {Md.Code(group.Label)}, {(group.Required ? "required" : "optional")}";
        if (group.Description.Length > 0)
        {
            note += $" — {Md.Text(group.Description)}";
        }

        return string.Join(' ', group.Schemas.SelectMany(StructureNotes).Prepend(note));
    }

    private SchemaKey CreateKey(string path, string anchor, SchemaMember member, IReadOnlyList<SchemaNode> branches, IReadOnlyList<VariantGroup>? groups, List<string> notes)
    {
        var schemas = member.Schemas;
        return new SchemaKey(
            path,
            anchor,
            SchemaNode.Union(schemas.Select(schema => schema.TypeLabel(root))),
            member.Required,
            DescriptionOf(member, groups),
            [.. schemas.SelectMany(schema => schema.Values()).Distinct(StringComparer.Ordinal)],
            schemas.Select(schema => schema.Find("default")).FirstOrDefault(value => value is not null) is { } value ? SchemaNode.Literal(value) : null,
            schemas.Select(schema => schema.Find("pattern")?.GetValue<string>()).FirstOrDefault(pattern => pattern is not null),
            notes,
            IsConstant(member),
            RequiredDetailOf(member, branches, groups));
    }

    private static bool IsConstant(SchemaMember member) => member.Schemas.All(schema => schema.Find("const") is not null);

    private static string DescriptionOf(SchemaMember member, IReadOnlyList<VariantGroup>? groups)
    {
        var described = member.Schemas.Select(schema => schema.Description).Where(text => text.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (groups is not null && described.Count > 1)
        {
            return VariesDescription;
        }

        if (described.Count > 0)
        {
            return described[0];
        }

        return member.IsVariantMember && IsConstant(member) ? SelectsVariantDescription : "";
    }

    private static string? RequiredDetailOf(SchemaMember member, IReadOnlyList<SchemaNode> branches, IReadOnlyList<VariantGroup>? groups)
    {
        if (groups is not null && groups.Select(group => group.Required).Distinct().Count() > 1)
        {
            return RequiredVaries;
        }

        return member.Required && SchemaVariants.IsPartial(member, branches) ? RequiredInVariant : null;
    }

    private void Expand(string path, string anchor, SchemaNode schema, List<string> notes, string notePrefix)
    {
        if (!schema.HasChildren(root))
        {
            return;
        }

        if (schema.Definition is { } definition)
        {
            var scope = (DescriptorPageSplit.PageOf(path), definition);
            if (_expanded.TryGetValue(scope, out var first))
            {
                notes.Add($"{notePrefix}Same shape as [{Md.Code(first.Path)}](#{first.Anchor}).");
                return;
            }

            _expanded[scope] = (path, anchor);
        }

        WalkChildren(path, anchor, schema, notes, notePrefix);
    }

    private void WalkChildren(string path, string anchor, SchemaNode parent, List<string> notes, string notePrefix)
    {
        var conditions = ReadConditions(path, parent);
        var branches = parent.ObjectBranches(root);
        foreach (var member in Members(parent, branches))
        {
            var memberNotes = SchemaVariants.NoteFor(member, branches, root) is { } variant ? [variant] : new List<string>();
            memberNotes.AddRange(conditions.GetValueOrDefault(member.Name) ?? []);
            Visit($"{path}.{member.Name}", $"{anchor}.{member.Name}", member, branches, memberNotes);
        }

        if (SchemaNode.Resolve(parent.FindObject("additionalProperties"), root) is { } entry)
        {
            Expand(path + EntryPlaceholder, anchor, entry, notes, notePrefix);
        }

        if (SchemaNode.Resolve(parent.FindObject("items"), root) is { } item)
        {
            Expand(path + ItemPlaceholder, anchor, item, notes, notePrefix);
        }
    }

    private List<SchemaMember> Members(SchemaNode parent, IReadOnlyList<SchemaNode> branches)
    {
        var members = new List<SchemaMember>();
        AddMembers(members, parent, SchemaVariants.Direct);
        for (var index = 0; index < branches.Count; index++)
        {
            AddMembers(members, branches[index], index);
        }

        return members;
    }

    private void AddMembers(List<SchemaMember> members, SchemaNode owner, int source)
    {
        var required = owner.FindStrings("required");
        foreach (var (name, value) in owner.FindObject("properties") ?? [])
        {
            if (SchemaNode.Resolve(value, root) is not { } schema)
            {
                continue;
            }

            var member = members.Find(existing => existing.Name == name);
            if (member is null)
            {
                members.Add(member = new SchemaMember(name));
            }

            member.Add(schema, required.Contains(name), source);
        }
    }

    private Dictionary<string, List<string>> ReadConditions(string path, SchemaNode parent)
    {
        var notes = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var conditions = parent.FindObjects("allOf").ToList();
        if (parent.Find("if") is not null)
        {
            conditions.Add(new JsonObject { ["if"] = parent.Find("if")!.DeepClone(), ["then"] = parent.Find("then")?.DeepClone() });
        }

        foreach (var condition in conditions.Where(condition => !SchemaConditions.TryRead(condition, notes)))
        {
            AddUnrecognised($"{path}: {condition.ToJsonString()}");
        }

        return notes;
    }

    private void AddUnrecognised(string entry)
    {
        if (!_unrecognised.Contains(entry))
        {
            _unrecognised.Add(entry);
        }
    }

    private IEnumerable<string> StructureNotes(SchemaNode schema)
    {
        if (SchemaNode.Resolve(schema.FindObject("additionalProperties"), root) is { Description.Length: > 0 } entry)
        {
            yield return $"Each entry: {Md.Text(entry.Description)}";
        }

        if (SchemaNode.Resolve(schema.FindObject("propertyNames"), root)?.Find("pattern")?.GetValue<string>() is { } pattern)
        {
            yield return $"Names match {Md.Code(pattern)}.";
        }

        foreach (var (name, _) in (schema.FindObject("properties") ?? []).Where(property => property.Value is JsonValue))
        {
            yield return $"Reserved name: {Md.Code(name)} is not allowed.";
        }
    }

    private static string PatternNote(string pattern, JsonNode? schema)
    {
        var description = (schema as JsonObject)?["description"]?.GetValue<string>();
        var note = $"Keys matching {Md.Code(pattern)} are accepted.";
        return description is null ? note : $"{note} {Md.Text(description)}";
    }
}
