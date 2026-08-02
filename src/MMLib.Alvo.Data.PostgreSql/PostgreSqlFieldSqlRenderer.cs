using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IFieldSqlRenderer"/>. The three two-valued members come from the port's
/// default interface members, whose defaults already carry the <c>COALESCE(…, FALSE)</c> shape
/// PostgreSQL accepts in boolean position — a dialect only overrides them when it has no boolean type
/// (T-SQL).
/// </summary>
public sealed class PostgreSqlFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc/>
    public string TrueLiteral => "TRUE";

    /// <inheritdoc/>
    public string FalseLiteral => "FALSE";

    /// <inheritdoc/>
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(fieldName);
    }

    /// <inheritdoc/>
    public string RenderParameter(string parameterName) => "@" + parameterName;

    /// <inheritdoc/>
    public string RenderCaseInsensitiveLike(string left, string right) => $"{left} ILIKE {right}";
}
