using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables (<c>AlvoOptions.SchemaPrefix</c>
/// is a table-name prefix, not a database schema), a standard <c>CAST</c> around the store type EF
/// resolved, and a real row lock.
/// </summary>
public sealed class PostgreSqlSqlDialect : IAlvoSqlDialect
{
    /// <inheritdoc/>
    public string RowLockHint => " FOR UPDATE";

    /// <inheritdoc/>
    public string RenderTable(EntitySchema entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    /// <inheritdoc/>
    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    /// <inheritdoc/>
    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        return $"CAST(NULL AS {storeType})";
    }
}
