namespace MMLib.Alvo.Expressions;

/// <summary>
/// A rendered SQL scalar expression and its bound parameters — the result of rendering a Computed
/// expression's value, e.g. for a generated column. Unlike <see cref="SqlPredicate"/> this is not
/// two-valued (it is not necessarily boolean at all), and it is never wrapped in <c>COALESCE</c>: a
/// generated column has no caller to deny. Deliberately not a positional record, for the same
/// forgery-resistance reason as <see cref="SqlPredicate"/>.
/// </summary>
public sealed record SqlExpression
{
    /// <summary>Initializes a new instance of the <see cref="SqlExpression"/> class.</summary>
    /// <param name="sql">The rendered SQL scalar expression.</param>
    /// <param name="parameters">The parameter values <paramref name="sql"/> references by name.</param>
    public SqlExpression(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        Sql = sql;
        Parameters = parameters;
    }

    /// <summary>Gets the rendered SQL scalar expression.</summary>
    public string Sql { get; }

    /// <summary>Gets the parameter values <see cref="Sql"/> references by name.</summary>
    public IReadOnlyDictionary<string, object?> Parameters { get; }
}
