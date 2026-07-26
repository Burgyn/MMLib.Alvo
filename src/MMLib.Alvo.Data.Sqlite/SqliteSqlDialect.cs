using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite;

/// <summary>
/// SQLite's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables, a standard <c>CAST</c> around the
/// store type EF resolved, and no row lock — SQLite serializes write transactions instead.
/// </summary>
public sealed class SqliteSqlDialect : IAlvoSqlDialect
{
    /// <inheritdoc/>
    public string RowLockHint => string.Empty;

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
