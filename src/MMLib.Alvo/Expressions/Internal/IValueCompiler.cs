using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions.Internal;

/// <summary>
/// Compiles an expression whose result is a value of one stated type rather than a profile's own result — a timer's
/// <c>at</c>, a <see cref="CelValueType.Timestamp"/> in the <see cref="CelProfile.Condition"/> profile.
/// </summary>
/// <remarks>
/// Internal and beside <see cref="ICelCompiler"/>, not on it: no public profile states a value type, and a new profile
/// would widen a frozen enum. A host that replaces <see cref="ICelCompiler"/> with a compiler that is not this one has no
/// <c>timers</c> (apply refuses them by name).
/// </remarks>
internal interface IValueCompiler
{
    /// <summary>Compiles <paramref name="source"/> under <paramref name="profile"/>, requiring a <paramref name="expected"/> result.</summary>
    /// <param name="source">The authored CEL source.</param>
    /// <param name="profile">The profile whose grammar the source is written in.</param>
    /// <param name="entity">The entity whose fields the source reads.</param>
    /// <param name="expected">The one result type accepted.</param>
    CelCompilationResult CompileValue(string source, CelProfile profile, EntitySchema entity, CelValueType expected);
}
