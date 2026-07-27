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
    /// <remarks>
    /// SQLite has no locking clause in either mode: a write transaction takes a database-wide lock, so a
    /// pre-image read and the write that follows it are already serialized against another writer. The
    /// answer is therefore the same for both mutations, and it is <see cref="string.Empty"/> rather than a
    /// clause the engine would reject.
    /// </remarks>
    public string RowLockClause(PreImageMutation mutation) => string.Empty;

    /// <inheritdoc/>
    /// <remarks>
    /// <paramref name="lockedPreImageFor"/> is ignored, and that is the honest answer rather than a missing
    /// one: SQLite expresses row locking in neither position — not as a trailing clause and not as a table
    /// hint — because a write transaction already takes a database-wide lock. A pre-image read therefore
    /// renders exactly the same table source as an ordinary one.
    /// </remarks>
    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor)
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
