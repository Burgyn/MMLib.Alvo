using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// A CEL expression that has passed the type checker and the profile filter: every
/// <see cref="CelFieldRef"/> in <see cref="Root"/> carries its resolved <see cref="CelValueType"/>,
/// every node is legal in <see cref="Profile"/>, and the whole tree's <see cref="ResultType"/>
/// matches what that profile requires.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="CompiledExpression"/> is only ever produced by a successful
/// <see cref="ICelCompiler.Compile"/>, so a renderer may assume it is type-checked and
/// in-profile — it never needs to re-validate the tree it renders. The constructor is
/// <see langword="internal"/> (the core is granted access via <c>InternalsVisibleTo</c>), which
/// prevents a provider or host from <em>accidentally</em> assembling one from a raw, unchecked parser
/// tree — or <c>with</c>-mutating <see cref="Root"/> back into one — and so re-introducing an untyped
/// <see cref="CelValueType.Null"/> field reference.
/// </para>
/// <para>
/// <strong>This is an encapsulation boundary, not a trust boundary.</strong> These assemblies are
/// unsigned, so <c>InternalsVisibleTo</c> stops nothing a determined caller cannot do anyway —
/// reflection reaches an internal constructor, and an attacker who can load code into the host has
/// already won. What it buys is that the compiler is the only <em>reachable</em> way to make one, so
/// no honest mistake produces an unchecked tree the renderer would trust.
/// </para>
/// <para>
/// Any cache keyed on a compiled expression (to avoid recompiling a rule on every request) must be
/// keyed on the descriptor's revision, not only the entity name and source text: a
/// <see cref="CompiledExpression"/> holds a specific <see cref="Entity"/> snapshot, and a cache entry
/// that outlives a re-apply would render against a stale schema — silently wrong if a column was
/// renamed and the old name reused for something else.
/// </para>
/// </remarks>
public sealed record CompiledExpression
{
    /// <summary>Initializes a new instance of the <see cref="CompiledExpression"/> class.</summary>
    /// <param name="root">The type-checked, profile-filtered expression tree.</param>
    /// <param name="profile">The profile this expression was compiled against.</param>
    /// <param name="resultType">The runtime type the whole expression evaluates to.</param>
    /// <param name="source">The original CEL source this expression was compiled from.</param>
    /// <param name="entity">The entity <paramref name="root"/> was checked against.</param>
    internal CompiledExpression(CelNode root, CelProfile profile, CelValueType resultType, string source, EntitySchema entity)
    {
        Root = root;
        Profile = profile;
        ResultType = resultType;
        Source = source;
        Entity = entity;
    }

    /// <summary>Gets the type-checked, profile-filtered expression tree.</summary>
    public CelNode Root { get; }

    /// <summary>Gets the profile this expression was compiled against.</summary>
    public CelProfile Profile { get; }

    /// <summary>Gets the runtime type the whole expression evaluates to.</summary>
    public CelValueType ResultType { get; }

    /// <summary>Gets the original CEL source this expression was compiled from.</summary>
    public string Source { get; }

    /// <summary>
    /// Gets the entity <see cref="Root"/> was checked against — a SQL renderer needs the full
    /// schema, not only its name, to resolve a field to a physical column or, on a dynamic entity
    /// (F7), a JSON path. Use <c>Entity.Name</c> where only the name is needed; the type no longer
    /// exposes a separate <c>EntityName</c> string alongside it.
    /// </summary>
    public EntitySchema Entity { get; }
}
