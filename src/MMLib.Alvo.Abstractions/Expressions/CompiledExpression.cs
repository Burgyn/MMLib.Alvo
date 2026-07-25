namespace MMLib.Alvo.Expressions;

/// <summary>
/// A CEL expression that has passed the type checker and the profile filter: every
/// <see cref="CelFieldRef"/> in <see cref="Root"/> carries its resolved <see cref="CelValueType"/>,
/// every node is legal in <see cref="Profile"/>, and the whole tree's <see cref="ResultType"/>
/// matches what that profile requires.
/// </summary>
/// <remarks>
/// A <see cref="CompiledExpression"/> is only ever produced by a successful
/// <see cref="ICelCompiler.Compile"/>, so a renderer may assume it is type-checked and
/// in-profile — it never needs to re-validate the tree it renders. The constructor is
/// <see langword="internal"/> (the core is granted access via <c>InternalsVisibleTo</c>), so no
/// provider or host can assemble one from a raw, unchecked parser tree — or <c>with</c>-mutate
/// <see cref="Root"/> back into one — re-introducing an untyped <see cref="CelValueType.Null"/>
/// field reference past this trust boundary.
/// </remarks>
public sealed record CompiledExpression
{
    /// <summary>Initializes a new instance of the <see cref="CompiledExpression"/> class.</summary>
    /// <param name="root">The type-checked, profile-filtered expression tree.</param>
    /// <param name="profile">The profile this expression was compiled against.</param>
    /// <param name="resultType">The runtime type the whole expression evaluates to.</param>
    /// <param name="source">The original CEL source this expression was compiled from.</param>
    /// <param name="entityName">The name of the entity <paramref name="root"/> was checked against.</param>
    internal CompiledExpression(CelNode root, CelProfile profile, CelValueType resultType, string source, string entityName)
    {
        Root = root;
        Profile = profile;
        ResultType = resultType;
        Source = source;
        EntityName = entityName;
    }

    /// <summary>Gets the type-checked, profile-filtered expression tree.</summary>
    public CelNode Root { get; }

    /// <summary>Gets the profile this expression was compiled against.</summary>
    public CelProfile Profile { get; }

    /// <summary>Gets the runtime type the whole expression evaluates to.</summary>
    public CelValueType ResultType { get; }

    /// <summary>Gets the original CEL source this expression was compiled from.</summary>
    public string Source { get; }

    /// <summary>Gets the name of the entity <see cref="Root"/> was checked against.</summary>
    public string EntityName { get; }
}
