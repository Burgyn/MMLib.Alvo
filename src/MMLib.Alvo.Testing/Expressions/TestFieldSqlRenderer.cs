using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Testing;

/// <summary>
/// A deliberately dumb, predictable <see cref="IFieldSqlRenderer"/>: <c>"quoted"</c> identifiers,
/// <c>@pN</c> parameters, <c>TRUE</c>/<c>FALSE</c> literals, <c>UPPER(a) LIKE UPPER(b)</c> for a
/// case-insensitive comparison. Used by the core's own renderer snapshots and by the differential
/// test that proves the SQL and in-memory backends agree — no real dialect quirk should ever leak
/// into either.
/// </summary>
public sealed class TestFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc />
    public string TrueLiteral => "TRUE";

    /// <inheritdoc />
    public string FalseLiteral => "FALSE";

    /// <inheritdoc />
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(fieldName);
        return $"\"{fieldName.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
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
        return $"UPPER({left}) LIKE UPPER({right})";
    }

    /// <inheritdoc />
    /// <remarks>
    /// Overridden — and visibly, as <c>CAST(… AS numeric)</c> — on purpose, even though this fake has no real
    /// storage that needs repairing. With the port's identity default, a renderer that stopped calling this
    /// member would produce byte-identical SQL, so every assertion and every golden baseline written against
    /// this renderer would keep passing while the repair that keeps a decimal comparison numeric on SQLite
    /// quietly disappeared. A visible wrapper makes the call site part of the frozen text: remove it and a
    /// named test fails.
    /// </remarks>
    public (string Left, string Right) RenderComparableOperands(string left, string right, CelValueType type)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return type == CelValueType.Decimal ? (AsNumeric(left), AsNumeric(right)) : (left, right);
    }

    private static string AsNumeric(string sql) => $"CAST({sql} AS numeric)";
}
