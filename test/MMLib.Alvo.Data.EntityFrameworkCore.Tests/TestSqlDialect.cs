using MMLib.Alvo.Schema;

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

    public string RenderTable(EntitySchema entity)
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
}
