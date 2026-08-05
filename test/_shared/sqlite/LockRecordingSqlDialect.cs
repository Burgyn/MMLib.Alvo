using MMLib.Alvo.Data.EntityFrameworkCore;
using MMLib.Alvo.Schema;
using System.Data.Common;

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

    /// <summary>Forwarded unchanged: this wrapper records locks, it decides nothing about a constraint.</summary>
    /// <param name="failure">The exception the write raised.</param>
    public SqlConstraintViolation? DecodeConstraintViolation(DbException failure) =>
        _inner.DecodeConstraintViolation(failure);

    /// <summary>
    /// Forwards the paging window clause through the interface, because it is a default interface member
    /// SQLite does not override — the point of forwarding is that this wrapper adds no dialect decision of
    /// its own.
    /// </summary>
    public string RowWindowClause(string rowCountParameterMarker, string? rowOffsetParameterMarker = null) =>
        ((IAlvoSqlDialect)_inner).RowWindowClause(rowCountParameterMarker, rowOffsetParameterMarker);

    /// <summary>
    /// Forwarded, and <b>not optional</b>: both generated-column members ship with a refusing default
    /// (<see langword="null"/>, <see langword="false"/>), so a wrapper that merely forgot them would silently
    /// turn every fixture built on it into an engine that "cannot express a generated column" and would make the
    /// whole computed suite refuse at apply — while looking like a wrapper that only records locks.
    /// </summary>
    /// <param name="columnName">The column's name.</param>
    /// <param name="storeType">The column's EF-resolved store type.</param>
    /// <param name="renderedExpression">The rendered SQL scalar expression.</param>
    public string? GeneratedColumnDefinition(string columnName, string storeType, string renderedExpression) =>
        _inner.GeneratedColumnDefinition(columnName, storeType, renderedExpression);

    /// <inheritdoc cref="IAlvoSqlDialect.MigrationFraming"/>
    /// <remarks>
    /// Forwarded for the same reason the two generated-column members are: the default is "no framing", and a
    /// wrapper that forgot it would silently take SQLite's foreign-key suspension away from every migration run
    /// through this fixture — which is data loss on a table rebuild, not a failing test.
    /// </remarks>
    public MigrationBatchFraming MigrationFraming => _inner.MigrationFraming;
}
