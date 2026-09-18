using MMLib.Alvo.Expressions;

namespace MMLib.Alvo.Rules;

/// <summary>
/// The descriptor's <c>access</c> block, compiled: three independent predicates over the caller, each
/// <see langword="null"/> when the descriptor declares nothing for that level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three independent predicates, not a hierarchy.</b> A caller matching <see cref="Admin"/> and not
/// <see cref="Viewer"/> is an administrator; a caller matching <see cref="Viewer"/> alone is a viewer.
/// The hierarchy lives one layer up, in what a management level is allowed to <em>do</em> — never in
/// whether one predicate implies another.
/// </para>
/// <para>
/// <b>A <see langword="null"/> level matches nobody</b>, exactly as a rule not configured for an
/// operation denies that operation rather than meaning "no restriction". A descriptor with no
/// <c>access</c> block at all therefore leaves only the bootstrap administrator, which is default-deny
/// and is usable: the first-run wizard works, and nobody else gets in until the descriptor says so.
/// </para>
/// </remarks>
/// <param name="Admin">Who may fully administer this project, including its settings.</param>
/// <param name="Developer">Who may edit this project's schema, rules and automation.</param>
/// <param name="Viewer">Who may read this project's data and configuration.</param>
internal sealed record ManagementAccessCatalog(
    CompiledExpression? Admin,
    CompiledExpression? Developer,
    CompiledExpression? Viewer)
{
    /// <summary>Gets the catalogue of a descriptor that declares no <c>access</c> block.</summary>
    internal static ManagementAccessCatalog Empty { get; } = new(null, null, null);

    /// <summary>
    /// Gets a value indicating whether this descriptor grants management access to nobody but the
    /// bootstrap administrator.
    /// </summary>
    internal bool IsEmpty => Admin is null && Developer is null && Viewer is null;
}
