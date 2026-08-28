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

    /// <summary>
    /// A before-hook <c>mutate</c> value expression: evaluated against the candidate row inside the
    /// write transaction, and written as a bound parameter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Interpreter-only, and that is a guarantee rather than an accident.</b> A <see cref="Mutate"/>
    /// expression is never handed to the SQL predicate renderer, so this profile adds no
    /// <see cref="IFieldSqlRenderer"/> member — the seam every storage driver implements is untouched,
    /// including the T-SQL fake that proves the seam is sufficient — no per-engine golden snapshot, and
    /// no row to the differential backend test, because there is no second backend to differ from.
    /// </para>
    /// <para>
    /// <b>The two-valued rendering rule does not apply here, and that is a consequence rather than an
    /// exemption.</b> The null collapse is a rule both backends must <em>agree</em> on; with one backend
    /// there is nothing to agree with, so <see cref="Mutate"/> inherits the interpreter's semantics
    /// unchanged. The moment somebody proposes rendering one of these to SQL, the two-valued fold and the
    /// collation caveat both come back into scope — which is why the renderer refuses this profile's nodes
    /// by name rather than falling through a default arm.
    /// </para>
    /// <para>
    /// <b>It is the one profile with a function allow-list</b>, and the list is exactly two entries:
    /// <c>lowerAscii(x)</c> and <c>now()</c>. Every other identifier followed by <c>(</c> is still
    /// refused, in this profile as in the others.
    /// </para>
    /// </remarks>
    Mutate,
}
