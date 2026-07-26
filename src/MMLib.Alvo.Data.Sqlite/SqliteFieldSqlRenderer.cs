using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Expressions;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// SQLite's <see cref="IFieldSqlRenderer"/>. The three two-valued members come from the port's default
/// interface members, whose defaults already carry the <c>COALESCE(…, 0)</c> shape SQLite accepts in
/// boolean position — a dialect only overrides them when it has no boolean type (T-SQL).
/// </summary>
public sealed class SqliteFieldSqlRenderer : IFieldSqlRenderer
{
    /// <inheritdoc/>
    public string TrueLiteral => "1";

    /// <inheritdoc/>
    public string FalseLiteral => "0";

    /// <inheritdoc/>
    public string RenderField(EntitySchema entity, string fieldName)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(fieldName);
    }

    /// <inheritdoc/>
    public string RenderParameter(string parameterName) => "@" + parameterName;

    /// <inheritdoc/>
    public string RenderCaseInsensitiveLike(string left, string right) => $"UPPER({left}) LIKE UPPER({right})";
}
