using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing;

/// <summary>
/// A T-SQL-shaped <see cref="IFieldSqlRenderer"/>, standing in for the SQL Server / Azure SQL driver
/// §0 principle 3 requires the core to support. T-SQL has no boolean type and no boolean-valued
/// expression: a <c>bit</c> column is a <em>value</em>, never a predicate, so
/// <c>COALESCE(&lt;predicate&gt;, 0)</c> — the shape PostgreSQL and SQLite use to collapse a
/// three-valued predicate — is not even parseable there. This fake exists to prove the seam is
/// sufficient: it implements <b>only</b> <see cref="IFieldSqlRenderer"/>, overriding the two-valued
/// members, and needs no change to the structural renderer.
/// </summary>
/// <remarks>
/// Public and shipped here rather than declared per test project: the seam it proves sufficient is now used
/// by the core's predicate renderer <em>and</em> by a storage driver's caller-filter renderer, and two copies
/// of the fake are how the two would come to be proved against different T-SQL.
/// </remarks>
public sealed class TSqlFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc />
    public string TrueLiteral => "1";

    /// <inheritdoc />
    public string FalseLiteral => "0";

    /// <summary>
    /// A predicate whose result may be <c>UNKNOWN</c>, folded back into a predicate: T-SQL's
    /// <c>CASE</c> is an expression, so the <c>ELSE</c> branch absorbs <c>UNKNOWN</c> and the outer
    /// <c>= 1</c> turns the resulting <c>bit</c> back into something a <c>WHERE</c> clause accepts.
    /// </summary>
    public string RenderTwoValued(string predicate) => $"(CASE WHEN {predicate} THEN 1 ELSE 0 END = 1)";

    /// <summary>A nullable <c>bit</c> column read as a predicate: default it in value position, then compare.</summary>
    public string RenderBooleanFieldAsPredicate(string booleanValue) => $"(COALESCE({booleanValue}, 0) = 1)";

    /// <summary>A boolean constant in predicate position — <c>WHERE 1</c> is not valid T-SQL, <c>WHERE (1 = 1)</c> is.</summary>
    public string RenderBooleanPredicate(bool value) => value ? "(1 = 1)" : "(1 = 0)";

    /// <inheritdoc />
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fieldName);
        return $"[{fieldName.Replace("]", "]]", StringComparison.Ordinal)}]";
    }

    /// <inheritdoc />
    public string RenderParameter(string parameterName)
    {
        ArgumentNullException.ThrowIfNull(parameterName);
        return $"@{parameterName}";
    }

    /// <inheritdoc />
    public string RenderCaseInsensitiveLike(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        return $"{left} LIKE {right}";
    }
}
