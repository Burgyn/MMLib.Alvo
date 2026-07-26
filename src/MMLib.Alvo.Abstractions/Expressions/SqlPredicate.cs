namespace MMLib.Alvo.Expressions;

/// <summary>
/// A rendered, two-valued SQL boolean predicate and its bound parameters — the framework's only
/// path from a <see cref="CompiledExpression"/> to a <c>WHERE</c>/<c>USING</c> clause. Deliberately
/// not a positional record: an explicit constructor plus get-only properties (no <c>init</c>) means
/// there is no <c>with</c>-mutation path that could re-inject text into <see cref="Sql"/> or swap
/// <see cref="Parameters"/> after the renderer produced them.
/// </summary>
public sealed record SqlPredicate
{
    /// <summary>Initializes a new instance of the <see cref="SqlPredicate"/> class.</summary>
    /// <param name="sql">The rendered, two-valued SQL boolean expression.</param>
    /// <param name="parameters">The parameter values <paramref name="sql"/> references by name.</param>
    public SqlPredicate(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        Sql = sql;
        Parameters = parameters;
    }

    /// <summary>Gets the rendered, two-valued SQL boolean expression.</summary>
    public string Sql { get; }

    /// <summary>Gets the parameter values <see cref="Sql"/> references by name.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }

    /// <summary>A predicate that always denies — the safe default when no rule applies.</summary>
    /// <param name="fields">
    /// The storage driver's field/dialect renderer, for its constant-false <em>predicate</em> — not its
    /// bare false literal, which on a dialect with no boolean type is a value a <c>WHERE</c> clause
    /// cannot evaluate.
    /// </param>
    public static SqlPredicate AlwaysFalse(IFieldSqlRenderer fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        return new SqlPredicate(fields.RenderBooleanPredicate(false), new Dictionary<string, object?>());
    }
}
