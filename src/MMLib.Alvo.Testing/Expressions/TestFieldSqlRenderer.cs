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
}
