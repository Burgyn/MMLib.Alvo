using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>Which way a <see cref="CelRelation"/> reads: through this row's ref, or from a child that refs this row.</summary>
internal enum CelRelationKind
{
    /// <summary><c>&lt;ref&gt;.&lt;field&gt;</c>: the related row is the one this row's ref points at.</summary>
    Forward,

    /// <summary><c>&lt;child&gt;.exists(x, …)</c>: the related rows are the children that ref this row.</summary>
    Reverse,
}

/// <summary>
/// A correlated read of one related entity: true when a row of <paramref name="Related"/> exists whose
/// <paramref name="RelatedKey"/> equals the outer row's <paramref name="OuterKey"/>, <paramref name="Body"/> holds over
/// it, and the caller may list it (<paramref name="Scope"/>). A <see cref="CelOuterFieldRef"/> in the body reads the
/// outer row; every other field reference reads the related one.
/// </summary>
/// <remarks>
/// Internal on purpose: the published AST gains no case, so a third-party walker or renderer meets an unknown node and
/// refuses it rather than mis-handling a read of another table. Only a rule or a lookup check compiled with a
/// <see cref="CelRelationCatalog"/> ever holds one.
/// </remarks>
/// <param name="Kind">Which way the relation reads.</param>
/// <param name="Related">The entity whose rows are read.</param>
/// <param name="RelatedKey">The related row's column correlated with the outer row.</param>
/// <param name="OuterKey">The outer row's column correlated with the related row.</param>
/// <param name="Via">The relation as the author spelled it: the ref field, or the child's (via-qualified) name.</param>
/// <param name="Body">The condition over the related row.</param>
/// <param name="Scope">The caller's list decision on <paramref name="Related"/>; <see langword="null"/> until bound.</param>
internal sealed record CelRelation(
    CelRelationKind Kind,
    EntitySchema Related,
    string RelatedKey,
    string OuterKey,
    string Via,
    CelNode Body,
    RelationScope? Scope = null) : CelNode;

/// <summary>A field of the outer row, read from inside a <see cref="CelRelation"/>'s body.</summary>
/// <param name="FieldName">The outer row's field.</param>
/// <param name="Type">The field's resolved type.</param>
internal sealed record CelOuterFieldRef(string FieldName, CelValueType Type) : CelNode;

/// <summary><c>has(field)</c> over the outer row, from inside a <see cref="CelRelation"/>'s body.</summary>
/// <param name="Field">The outer row's field whose presence is tested.</param>
internal sealed record CelOuterHas(CelOuterFieldRef Field) : CelNode;

/// <summary>
/// The related entity's <c>list</c> decision for one caller: <paramref name="Denied"/> matches no related row at all;
/// otherwise both predicates are ANDed inside the correlated read, and each may itself hold bound relations.
/// </summary>
/// <param name="Denied">Whether the caller may list no row of the related entity.</param>
/// <param name="Using">The related entity's bound <c>list</c> rule.</param>
/// <param name="TenantScope">The related entity's tenant scope, when it is tenant-scoped.</param>
internal sealed record RelationScope(bool Denied, CompiledExpression? Using, CompiledExpression? TenantScope)
{
    /// <summary>The scope of a related entity the caller may not list: no related row matches.</summary>
    internal static RelationScope Deny { get; } = new(true, null, null);
}
