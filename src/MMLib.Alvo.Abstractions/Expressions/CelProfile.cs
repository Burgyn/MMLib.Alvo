namespace MMLib.Alvo.Expressions;

/// <summary>
/// Which slot of the project descriptor a CEL expression was authored for. The profile determines
/// which <see cref="CelNode"/> kinds are legal — e.g. <see cref="CelChanged"/> and the
/// <see cref="CelRecordState.Old"/>/<see cref="CelRecordState.New"/> field qualifiers are legal only
/// in <see cref="Condition"/>.
/// </summary>
public enum CelProfile
{
    /// <summary>A row-level authorization rule (RLS-style <c>USING</c>/<c>WITH CHECK</c>).</summary>
    Rule,

    /// <summary>A computed-field expression.</summary>
    Computed,

    /// <summary>
    /// A hook condition (e.g. <c>hooks.beforeUpdate</c>) — the only profile where <c>old.</c>,
    /// <c>new.</c>, and <c>changed(field)</c> are legal.
    /// </summary>
    Condition,
}
