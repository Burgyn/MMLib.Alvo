namespace MMLib.Alvo.DocsGen.Schema;

internal sealed class SchemaMember(string name)
{
    internal string Name { get; } = name;

    internal List<SchemaNode> Schemas { get; } = [];

    internal List<int> Sources { get; } = [];

    internal List<bool> RequiredFlags { get; } = [];

    internal bool Required => RequiredFlags.Count > 0 && RequiredFlags.TrueForAll(flag => flag);

    internal bool IsVariantMember => !Sources.Contains(SchemaVariants.Direct);

    internal static SchemaMember Direct(string name, SchemaNode schema, bool required)
    {
        var member = new SchemaMember(name);
        member.Add(schema, required, SchemaVariants.Direct);
        return member;
    }

    internal void Add(SchemaNode schema, bool required, int source)
    {
        Schemas.Add(schema);
        RequiredFlags.Add(required);
        Sources.Add(source);
    }
}
