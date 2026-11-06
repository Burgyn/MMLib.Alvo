using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// Builds the <see cref="CelRelationCatalog"/> a rule compile follows relations through: forward targets are the
/// readable entities, and an <c>exists</c> may name a child that references its parent once, or
/// <c>&lt;child&gt;_via_&lt;field&gt;</c> for any of its references.
/// </summary>
internal static class RelationCatalogBuilder
{
    /// <summary>
    /// The catalog an apply compiles rules with: the schema's entities are readable, except the built-in
    /// <c>users</c>; a <c>storage: dynamic</c> entity never reaches the schema and is refused as such (#41).
    /// </summary>
    /// <param name="descriptor">The descriptor being applied, for the entities the schema leaves out.</param>
    /// <param name="schema">The applied schema.</param>
    internal static CelRelationCatalog From(AlvoDescriptor descriptor, SchemaModel schema) => From(
        [.. schema.Entities.Where(entity => entity.Name != RelationRefusals.UsersEntity)],
        descriptor.Entities
            .Where(entity => !DescriptorToSchemaMapper.IsPhysical(entity.Value))
            .ToDictionary(entity => entity.Key, _ => RelationRefusals.Dynamic, StringComparer.Ordinal));

    /// <summary>The catalog over <paramref name="readable"/>; every other entity is refused with its reason.</summary>
    /// <param name="readable">The declared, physical entities a relation may read.</param>
    /// <param name="unreadable">Each declared entity a rule may not read, and why (<see cref="RelationRefusals.Dynamic"/>).</param>
    internal static CelRelationCatalog From(IReadOnlyList<EntitySchema> readable, IReadOnlyDictionary<string, string> unreadable)
    {
        var entities = readable.ToDictionary(entity => entity.Name, StringComparer.Ordinal);
        var children = Children(readable, entities);
        var reverse = new Dictionary<(string Entity, string Name), (EntitySchema Child, string Via)>();
        var ambiguous = new Dictionary<(string Entity, string Name), IReadOnlyList<string>>();
        foreach (var (parent, child, references) in children)
        {
            AddChild(parent, child, references, reverse, ambiguous);
        }

        var refusals = new Refusals(entities, unreadable, reverse, ambiguous);
        return new CelRelationCatalog(entities, reverse, refusals.For);
    }

    /// <summary>Each (parent, child) pair and the child's refs to that parent, in declaration order.</summary>
    private static IEnumerable<(string Parent, EntitySchema Child, IReadOnlyList<string> References)> Children(
        IReadOnlyList<EntitySchema> readable, Dictionary<string, EntitySchema> entities) =>
        from child in readable
        from reference in child.Fields.Where(field => field.Reference is not null).GroupBy(field => field.Reference!.TargetEntity)
        where entities.ContainsKey(reference.Key)
        select (reference.Key, child, (IReadOnlyList<string>)[.. reference.Select(field => field.Name)]);

    private static void AddChild(
        string parent,
        EntitySchema child,
        IReadOnlyList<string> references,
        Dictionary<(string Entity, string Name), (EntitySchema Child, string Via)> reverse,
        Dictionary<(string Entity, string Name), IReadOnlyList<string>> ambiguous)
    {
        if (references.Count == 1)
        {
            reverse[(parent, child.Name)] = (child, references[0]);
        }
        else
        {
            ambiguous[(parent, child.Name)] = references;
        }

        foreach (var reference in references)
        {
            reverse[(parent, $"{child.Name}_via_{reference}")] = (child, reference);
        }
    }

    /// <summary>Why one (entity, member, field) cannot be followed, from what the catalog was built over.</summary>
    private sealed class Refusals(
        Dictionary<string, EntitySchema> entities,
        IReadOnlyDictionary<string, string> unreadable,
        Dictionary<(string Entity, string Name), (EntitySchema Child, string Via)> reverse,
        Dictionary<(string Entity, string Name), IReadOnlyList<string>> ambiguous)
    {
        internal CelCompilationError For(string entity, string member, string? field) =>
            field is null ? ForExists(entity, member) : ForPath(entity, member, field);

        private CelCompilationError ForPath(string entity, string member, string field)
        {
            var schema = entities.GetValueOrDefault(entity);
            var target = schema?.Fields.FirstOrDefault(candidate => candidate.Name == member)?.Reference?.TargetEntity;
            return target is null
                ? RelationRefusals.NotAReference(entity, member, field, RefsOf(schema))
                : RelationRefusals.Unreadable(target, ReasonFor(target), member);
        }

        private CelCompilationError ForExists(string entity, string member)
        {
            if (ambiguous.TryGetValue((entity, member), out var references))
            {
                return RelationRefusals.Ambiguous(entity, member, references);
            }

            var children = ChildrenOf(entity);
            return entities.ContainsKey(member)
                ? RelationRefusals.NotAChild(entity, member, children)
                : RelationRefusals.UnreadableChild(entity, member, ReasonFor(member), children);
        }

        /// <summary>The spellings an <c>exists</c> on <paramref name="entity"/> may use: a via spelling only where it is needed.</summary>
        private IEnumerable<string> ChildrenOf(string entity) =>
            from entry in reverse
            where entry.Key.Entity == entity
                && (entry.Key.Name == entry.Value.Child.Name || ambiguous.ContainsKey((entity, entry.Value.Child.Name)))
            select entry.Key.Name;

        private string ReasonFor(string target) => target == RelationRefusals.UsersEntity
            ? RelationRefusals.BuiltInUsers
            : unreadable.GetValueOrDefault(target, RelationRefusals.Undeclared);

        private static IEnumerable<string> RefsOf(EntitySchema? schema) =>
            schema?.Fields.Where(candidate => candidate.Reference is not null).Select(candidate => candidate.Name) ?? [];
    }
}
