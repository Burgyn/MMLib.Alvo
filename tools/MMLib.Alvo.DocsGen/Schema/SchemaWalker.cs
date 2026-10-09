using MMLib.Alvo.DocsGen.Markdown;
using System.Text.Json.Nodes;

namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaWalker(JsonObject root)
{
    private const string EntryPlaceholder = ".<name>";
    private const string ItemPlaceholder = "[]";

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
        Visit(topLevelKey, topLevelKey, [node], _root.FindStrings("required").Contains(topLevelKey), []);
        return [.. _keys];
    }

    private SchemaNode? TopLevel(string key) => SchemaNode.Resolve(_root.FindObject("properties")?[key], root);

    private void Visit(string path, string anchor, IReadOnlyList<SchemaNode> schemas, bool required, IReadOnlyList<string> conditionNotes)
    {
        if (!_emitted.Add(path))
        {
            return;
        }

        var notes = new List<string>();
        _keys.Add(CreateKey(path, anchor, schemas, required, notes));
        notes.AddRange(schemas.SelectMany(StructureNotes));
        notes.AddRange(conditionNotes);
        foreach (var schema in schemas)
        {
            Expand(path, anchor, schema, notes);
        }
    }

    private SchemaKey CreateKey(string path, string anchor, IReadOnlyList<SchemaNode> schemas, bool required, List<string> notes) => new(
        path,
        anchor,
        SchemaNode.Union(schemas.Select(schema => schema.TypeLabel(root))),
        required,
        schemas.Select(schema => schema.Description).FirstOrDefault(text => text.Length > 0) ?? "",
        [.. schemas.SelectMany(schema => schema.Values()).Distinct(StringComparer.Ordinal)],
        schemas.Select(schema => schema.Find("default")).FirstOrDefault(value => value is not null) is { } value ? SchemaNode.Literal(value) : null,
        schemas.Select(schema => schema.Find("pattern")?.GetValue<string>()).FirstOrDefault(pattern => pattern is not null),
        notes,
        schemas.All(schema => schema.Find("const") is not null));

    private void Expand(string path, string anchor, SchemaNode schema, List<string> notes)
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
                notes.Add($"Same shape as [{Md.Code(first.Path)}](#{first.Anchor}).");
                return;
            }

            _expanded[scope] = (path, anchor);
        }

        WalkChildren(path, anchor, schema, notes);
    }

    private void WalkChildren(string path, string anchor, SchemaNode parent, List<string> notes)
    {
        var conditions = ReadConditions(path, parent);
        var branches = parent.ObjectBranches(root);
        foreach (var member in Members(parent, branches))
        {
            var memberNotes = SchemaVariants.NoteFor(member.Name, member.Sources, branches, root) is { } variant ? [variant] : new List<string>();
            memberNotes.AddRange(conditions.GetValueOrDefault(member.Name) ?? []);
            Visit($"{path}.{member.Name}", $"{anchor}.{member.Name}", member.Schemas, member.Required, memberNotes);
        }

        if (SchemaNode.Resolve(parent.FindObject("additionalProperties"), root) is { } entry)
        {
            Expand(path + EntryPlaceholder, anchor, entry, notes);
        }

        if (SchemaNode.Resolve(parent.FindObject("items"), root) is { } item)
        {
            Expand(path + ItemPlaceholder, anchor, item, notes);
        }
    }

    private List<Member> Members(SchemaNode parent, IReadOnlyList<SchemaNode> branches)
    {
        var members = new List<Member>();
        AddMembers(members, parent, SchemaVariants.Direct);
        for (var index = 0; index < branches.Count; index++)
        {
            AddMembers(members, branches[index], index);
        }

        return members;
    }

    private void AddMembers(List<Member> members, SchemaNode owner, int source)
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
                members.Add(member = new Member(name));
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

    private sealed class Member(string name)
    {
        private readonly List<bool> _required = [];

        internal string Name { get; } = name;

        internal List<SchemaNode> Schemas { get; } = [];

        internal List<int> Sources { get; } = [];

        internal bool Required => _required.Count > 0 && _required.TrueForAll(flag => flag);

        internal void Add(SchemaNode schema, bool required, int source)
        {
            Schemas.Add(schema);
            _required.Add(required);
            Sources.Add(source);
        }
    }
}
