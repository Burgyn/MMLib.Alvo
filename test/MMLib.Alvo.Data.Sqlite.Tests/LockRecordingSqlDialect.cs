using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.Sqlite.Tests;

/// <summary>
/// <see cref="SqliteSqlDialect"/> with one difference: it records which <see cref="PreImageMutation"/> the
/// data path asked for a row lock for.
/// </summary>
/// <remarks>
/// SQLite has no locking clause, so its own answer is the empty string and a test on this engine cannot see
/// whether the lock was requested at all — while on PostgreSQL that request is the only thing stopping a
/// concurrent writer from changing the row between the <c>WITH CHECK</c> verdict and the write. Recording
/// the request makes the obligation provable on either engine.
/// </remarks>
internal sealed class LockRecordingSqlDialect : IAlvoSqlDialect
{
    private readonly SqliteSqlDialect _inner = new();
    private readonly List<PreImageMutation> _requested = [];
    private readonly Lock _gate = new();

    internal IReadOnlyList<PreImageMutation> RequestedLocks
    {
        get
        {
            lock (_gate)
            {
                return [.. _requested];
            }
        }
    }

    public string RowLockClause(PreImageMutation mutation)
    {
        lock (_gate)
        {
            _requested.Add(mutation);
        }

        return _inner.RowLockClause(mutation);
    }

    public string RenderTable(EntitySchema entity, PreImageMutation? lockedPreImageFor) =>
        _inner.RenderTable(entity, lockedPreImageFor);

    public string RenderColumn(string columnName) => _inner.RenderColumn(columnName);

    public string RenderNullProjection(string storeType) => _inner.RenderNullProjection(storeType);

    /// <summary>
    /// Forwards the row-limit clause through the interface, because it is a default interface member SQLite
    /// does not override — the point of forwarding is that this wrapper adds no dialect decision of its own.
    /// </summary>
    public string RowLimitClause(string rowCountParameterMarker) =>
        ((IAlvoSqlDialect)_inner).RowLimitClause(rowCountParameterMarker);
}
