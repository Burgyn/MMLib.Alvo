namespace MMLib.Alvo.Expressions;

/// <summary>
/// Renders a compiled CEL expression to SQL — Alvo's <c>USING</c>/generated-column backend, written
/// to agree term-for-term with the in-memory <c>WITH CHECK</c> backend (see the interpreter's class
/// remarks) so the two never disagree on any well-typed expression and row.
/// </summary>
public interface IPredicateRenderer
{
    /// <summary>
    /// Renders a Rule or Condition expression's boolean verdict to a two-valued SQL predicate: the
    /// returned <see cref="SqlPredicate.Sql"/> evaluates to true or false and never SQL's
    /// three-valued <c>UNKNOWN</c>, and it contains no value from <paramref name="expression"/>'s
    /// source text — every literal and every context value leaves as a named parameter, or, for a
    /// value already known at render time (role membership), as a bare boolean literal.
    /// </summary>
    /// <param name="expression">A compiled Rule or Condition expression.</param>
    /// <param name="context">The caller/tenant context <c>@user</c>/<c>@tenant</c> resolve against.</param>
    /// <param name="fields">The storage driver's field/dialect renderer.</param>
    /// <param name="parameterPrefix">
    /// The prefix every generated parameter name in this render carries, so a caller composing several
    /// predicates into one command can keep their names disjoint — see <see cref="SqlPredicate"/>, which
    /// owns that contract. Must be a plain identifier (an ASCII letter or <c>_</c> followed by letters,
    /// digits or <c>_</c>): it reaches the SQL text unparameterized, since a bind parameter's own name
    /// has no bind-parameter form.
    /// <para>
    /// The default is deliberately <c>alvo_p</c> rather than the obvious <c>p</c>, and a driver must
    /// not "simplify" it back: a rendered predicate is composed into a command that also carries the
    /// data-access layer's own parameters, and EF Core names those <c>p0</c>, <c>p1</c>, … . A
    /// <c>p</c>-prefixed render therefore collides, and the failure mode found in PR2's spike is
    /// silent rather than loud — EF keeps its own <c>p0</c> and renames the colliding one to
    /// <c>p00</c> while both occurrences in the SQL text still read <c>@p0</c>, so a caller-supplied
    /// value is substituted into the <b>security predicate</b>. On SQLite that returns the wrong rows
    /// with no error at all; on PostgreSQL it happens to throw only when the two values' types
    /// differ. Any prefix an ORM would not mint is safe; distinct prefixes per predicate
    /// (<c>alvo_u</c> / <c>alvo_c</c> / <c>alvo_t</c>) additionally keep one decision's three
    /// predicates disjoint from each other.
    /// </para>
    /// </param>
    /// <exception cref="InvalidOperationException"><paramref name="expression"/> was compiled for the Computed profile.</exception>
    /// <exception cref="ArgumentException"><paramref name="parameterPrefix"/> is not a plain identifier.</exception>
    /// <exception cref="NotSupportedException">
    /// <paramref name="expression"/> is a Condition tree that references <c>old.</c>/<c>new.</c> or
    /// <c>changed(...)</c>. A hook condition is evaluated entirely in-process by
    /// <c>CelInterpreter</c> — it never runs as a SQL predicate — so this is by design, not a gap: a
    /// Condition tree is only renderable here when it happens not to use those constructs.
    /// </exception>
    SqlPredicate Render(
        CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix = "alvo_p");

    /// <summary>
    /// Renders a Computed expression's scalar value to SQL. The result is <b>not</b> wrapped in
    /// <c>COALESCE</c> — a generated column has no caller, so there is nothing to deny — and no
    /// <see cref="AlvoContext"/> is accepted, since a Computed expression can never reference one.
    /// </summary>
    /// <param name="expression">A compiled Computed expression.</param>
    /// <param name="fields">The storage driver's field/dialect renderer.</param>
    /// <exception cref="InvalidOperationException"><paramref name="expression"/> was not compiled for the Computed profile.</exception>
    SqlExpression Render(CompiledExpression expression, IFieldSqlRenderer fields);

    /// <summary>
    /// Renders a Rule-profile expression that reads the row alone — no caller, no tenant, no now() — as a SQL predicate
    /// with every constant written inline, for a place that has no bind parameters: a CHECK constraint, a stored filter.
    /// </summary>
    /// <remarks>
    /// Internal until a second consumer needs it public (HX-11 Ruling 17): the core implements it and the EF driver reaches
    /// it through this assembly's <c>InternalsVisibleTo</c>, so a third-party renderer inherits this refusal.
    /// </remarks>
    /// <param name="expression">A compiled Rule-profile expression.</param>
    /// <param name="fields">The engine's field, literal and epoch rendering.</param>
    /// <returns>A two-valued SQL predicate with no parameters.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="expression"/> was not compiled for the Rule profile.</exception>
    /// <exception cref="NotSupportedException">The expression reads the caller, the tenant or the clock, or holds a constant this engine cannot write inline.</exception>
    internal string RenderConstraint(CompiledExpression expression, IFieldSqlRenderer fields) =>
        throw new NotSupportedException($"{GetType().Name} does not render constraints.");

    /// <summary>
    /// Renders a Rule expression whose relations are bound for this caller: each one becomes a correlated <c>EXISTS</c>
    /// over <paramref name="relations"/>' unlocked table, with the related <c>list</c> rule and tenant scope ANDed inside.
    /// </summary>
    /// <remarks>Internal, like <see cref="RenderConstraint"/>: a third-party renderer inherits this refusal.</remarks>
    /// <param name="expression">A compiled Rule expression whose relations <c>IPolicyEngine.Resolve</c> bound.</param>
    /// <param name="context">The caller.</param>
    /// <param name="fields">The engine's field rendering.</param>
    /// <param name="parameterPrefix">The prefix every parameter of this render, nested scopes included, carries.</param>
    /// <param name="relations">The driver's table source and the outer row a relation correlates with.</param>
    /// <returns>A two-valued SQL predicate.</returns>
    /// <exception cref="NotSupportedException">This renderer renders no relation.</exception>
    internal SqlPredicate Render(
        CompiledExpression expression, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix,
        RelationSql relations) =>
        throw new NotSupportedException($"{GetType().Name} does not render relation rules.");

    /// <summary>One bound relation term, alone: the <c>EXISTS</c> a write probe selects over a post-image.</summary>
    /// <param name="relation">A relation bound for this caller.</param>
    /// <param name="outer">The entity whose row the relation correlates with.</param>
    /// <param name="context">The caller.</param>
    /// <param name="fields">The engine's field rendering.</param>
    /// <param name="parameterPrefix">The prefix every parameter of this render carries.</param>
    /// <param name="relations">The driver's table source and the outer row, usually <see cref="RowSource.Image"/>.</param>
    /// <returns>A two-valued SQL predicate.</returns>
    /// <exception cref="NotSupportedException">This renderer renders no relation.</exception>
    internal SqlPredicate RenderRelation(
        CelRelation relation, Schema.EntitySchema outer, AlvoContext context, IFieldSqlRenderer fields, string parameterPrefix,
        RelationSql relations) =>
        throw new NotSupportedException($"{GetType().Name} does not render relation rules.");
}
