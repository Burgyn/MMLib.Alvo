using MMLib.Alvo.Schema;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore.Tests;

/// <summary>
/// A deliberately dumb <see cref="IAlvoSqlDialect"/>: quoted identifiers, a <c>CAST(NULL AS …)</c> that
/// echoes whatever store type it is handed, and a recognisable row-lock clause per mutation. It lets the
/// projection and statement-composer tests assert the composed text without a real engine, while still
/// proving the store type came from somewhere else — this dialect has no type table of its own to fall
/// back on.
/// </summary>
internal sealed class TestSqlDialect : IAlvoSqlDialect
{
    public string RowLockClause(PreImageMutation mutation) =>
        mutation == PreImageMutation.Delete ? "FOR TEST DELETE" : "FOR TEST";

    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return AlvoSqlIdentifier.Quote(entity.Name);
    }

    public string RenderColumn(string columnName) => AlvoSqlIdentifier.Quote(columnName);

    public string RenderNullProjection(string storeType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeType);
        return $"CAST(NULL AS {storeType})";
    }

    /// <summary>
    /// Nothing: this dialect has no engine behind it, so it has no constraint violation to recognise. The
    /// composer tests never write a row, so the honest answer is also the only reachable one.
    /// </summary>
    /// <param name="failure">The exception the write raised.</param>
    public SqlConstraintViolation? DecodeConstraintViolation(DbException failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return null;
    }
}
