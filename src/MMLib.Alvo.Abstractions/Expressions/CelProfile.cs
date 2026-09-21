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

    /// <summary>
    /// A management-access level: one of the descriptor's <c>access.admin</c> /
    /// <c>access.developer</c> / <c>access.viewer</c> predicates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>It has no row, and that is why it is a profile of its own rather than
    /// <see cref="Rule"/>.</b> <see cref="Rule"/> sees the current row <em>and</em> <c>@user</c>, so an
    /// access level written as <c>owner_id == @user.id</c> would compile under it and then have nothing
    /// to evaluate against. The table refuses every row-shaped construct here instead: field references
    /// of either state, <c>has(...)</c> and <c>changed(...)</c>.
    /// </para>
    /// <para>
    /// <b><c>@tenant</c> is excluded deliberately.</b> An access level is <em>project</em>-scoped by the
    /// frozen schema's own description, so admitting <c>@tenant</c> would make a project-level predicate
    /// answer differently per request — neither what the block says nor something an operator could
    /// reason about. <c>@user.id</c> and <c>@user.roles</c> are the whole of the context it sees, which
    /// is the closed set the schema already promised, and widening <c>@user</c> is additive, so a
    /// role-based level keeps compiling once typed claims land.
    /// </para>
    /// <para>
    /// <b>Interpreter-only</b>, for <see cref="Mutate"/>'s reason one subsystem over: there is no row,
    /// so there is nothing to push a predicate into. <c>SqlPredicateRenderer</c> refuses this profile by
    /// name rather than falling through, because — unlike <see cref="Mutate"/> — every construct an
    /// access level can contain is one it would otherwise render perfectly well.
    /// </para>
    /// </remarks>
    Access,
}
