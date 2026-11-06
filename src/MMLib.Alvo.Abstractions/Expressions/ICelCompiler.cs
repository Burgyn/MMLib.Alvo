using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Expressions;

/// <summary>
/// Turns authored CEL source into a <see cref="CompiledExpression"/> a renderer can trust — the
/// fail-fast boundary of Alvo's security core. An unknown column, an out-of-profile construct, a
/// type error, or a tree that nests too deeply is reported here, when the descriptor is applied,
/// with a fix suggestion — never at request time.
/// </summary>
public interface ICelCompiler
{
    /// <summary>Compiles CEL source against an entity's schema for a given profile.</summary>
    /// <param name="source">The CEL expression source.</param>
    /// <param name="profile">Which slot of the descriptor <paramref name="source"/> was authored for.</param>
    /// <param name="entity">The entity <paramref name="source"/> is checked against.</param>
    /// <returns>
    /// A successful result carrying a <see cref="CompiledExpression"/>, or a failed result
    /// carrying every problem found. Never throws for any <paramref name="source"/> string.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="source"/> or <paramref name="entity"/> is <see langword="null"/>.</exception>
    CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity);

    /// <summary>
    /// Compiles a rule or lookup-check source that may read related rows through <paramref name="relations"/>. A
    /// compiler that does not implement it ignores the catalog, so relation syntax stays refused: it fails closed.
    /// </summary>
    /// <param name="source">The CEL expression source.</param>
    /// <param name="profile">Which slot of the descriptor <paramref name="source"/> was authored for.</param>
    /// <param name="entity">The entity <paramref name="source"/> is checked against.</param>
    /// <param name="relations">The relations the source may follow.</param>
    internal CelCompilationResult Compile(string source, CelProfile profile, EntitySchema entity, CelRelationCatalog relations) =>
        Compile(source, profile, entity);
}
