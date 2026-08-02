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
}
