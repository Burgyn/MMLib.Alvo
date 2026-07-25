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
/// in-profile — it never needs to re-validate the tree it renders.
/// </remarks>
public sealed record CompiledExpression
{
    /// <summary>Gets the type-checked, profile-filtered expression tree.</summary>
    public required CelNode Root { get; init; }

    /// <summary>Gets the profile this expression was compiled against.</summary>
    public required CelProfile Profile { get; init; }

    /// <summary>Gets the runtime type the whole expression evaluates to.</summary>
    public required CelValueType ResultType { get; init; }

    /// <summary>Gets the original CEL source this expression was compiled from.</summary>
    public required string Source { get; init; }

    /// <summary>Gets the name of the entity <see cref="Root"/> was checked against.</summary>
    public required string EntityName { get; init; }
}
