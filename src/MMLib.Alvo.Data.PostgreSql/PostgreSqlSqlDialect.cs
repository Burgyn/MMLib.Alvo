using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.PostgreSql;

/// <summary>
/// PostgreSQL's <see cref="IAlvoSqlDialect"/>: unqualified quoted tables (<c>AlvoOptions.SchemaPrefix</c>
/// is a table-name prefix, not a database schema), a standard <c>CAST</c> around the store type EF
/// resolved, and a real row lock whose mode depends on the mutation
/// (see <see cref="IAlvoSqlDialect.RowLockClause"/>).
/// </summary>
public sealed class PostgreSqlSqlDialect : IAlvoSqlDialect
{
    private const string NoKeyUpdate = "FOR NO KEY UPDATE";
    private const string FullUpdate = "FOR UPDATE";

    /// <inheritdoc/>
    /// <remarks>
    /// An update provably never changes the row's key, so it takes the weaker mode PostgreSQL documents
    /// for exactly that case (<i>SELECT</i>, "The Locking Clause"); a delete removes the key, so it needs
    /// the stronger mode that also blocks the <c>FOR KEY SHARE</c> a concurrent foreign-key check would
    /// take (<i>Explicit Locking</i> §13.3.2, which defines <c>FOR NO KEY UPDATE</c> as the mode that does
    /// not block it).
    /// </remarks>
    public string RowLockClause(DataOperation operation) => operation switch
    {
        DataOperation.Update => NoKeyUpdate,
        DataOperation.Delete => FullUpdate,
        _ => throw new ArgumentOutOfRangeException(
            nameof(operation), operation, "Only an update or a delete reads a pre-image that can be locked."),
    };

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
