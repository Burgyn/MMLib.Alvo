using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// What a rule or lookup-check compile may follow: the entities a relation may read, and the children that reference
/// each entity. A compile given no catalog parses no relation syntax at all, so every other CEL slot keeps its refusal.
/// </summary>
/// <param name="entities">The readable entities by name: declared, physical and not the built-in users.</param>
/// <param name="reverse">Each (parent, spelling) an <c>exists</c> may name: the child and the child's ref to the parent.</param>
/// <param name="refusal">
/// Why (entity, member, field) cannot be followed: the member is no ref, its target is unreadable, or the child is
/// ambiguous or no child; <c>field</c> is <see langword="null"/> for an <c>exists</c> receiver.
/// </param>
internal sealed class CelRelationCatalog(
    IReadOnlyDictionary<string, EntitySchema> entities,
    IReadOnlyDictionary<(string Entity, string Name), (EntitySchema Child, string Via)> reverse,
    Func<string, string, string?, CelCompilationError> refusal)
{
    /// <summary>The readable entity of that name, or <see langword="null"/>.</summary>
    /// <param name="name">The entity's name.</param>
    internal EntitySchema? Entity(string name) => entities.GetValueOrDefault(name);

    /// <summary>The child an <c>exists</c> on <paramref name="entity"/> spelled <paramref name="name"/> reads, or <see langword="null"/>.</summary>
    /// <param name="entity">The outer entity.</param>
    /// <param name="name">The receiver as written: a child's name or <c>&lt;child&gt;_via_&lt;field&gt;</c>.</param>
    internal (EntitySchema Child, string Via)? Reverse(string entity, string name) =>
        reverse.TryGetValue((entity, name), out var hit) ? hit : null;

    /// <summary>The refusal for a member that cannot be followed; its position is the caller's to set.</summary>
    /// <param name="entity">The outer entity.</param>
    /// <param name="member">The ref, or the <c>exists</c> receiver.</param>
    /// <param name="field">The field read through the ref, or <see langword="null"/> for an <c>exists</c> receiver.</param>
    internal CelCompilationError Refusal(string entity, string member, string? field) => refusal(entity, member, field);

    /// <summary>
    /// Whether the compile lifts each forward comparison into its own relation term, as a rule needs; a lookup check
    /// turns off the lift, because its whole condition is the body of one relation.
    /// </summary>
    internal bool LiftsForwardPaths { get; init; } = true;
}
