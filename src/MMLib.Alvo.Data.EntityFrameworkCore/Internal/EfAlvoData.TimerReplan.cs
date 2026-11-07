using Microsoft.EntityFrameworkCore.Storage;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Schema;

using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The system re-plan of the descriptor's timers over the rows an entity already stores: one keyset page per transaction,
/// its rows locked, then the timers already armed for them read and changed on the same connection.
/// </summary>
internal sealed partial class EfAlvoData : IEntityTimerReplanner
{
    /// <inheritdoc/>
    /// <remarks>
    /// <para>
    /// <b>The rows are read through the composer with the decision the core built for the re-plan</b> — no tenant scope and an
    /// always-true predicate, unmasked — so every row of the entity is planned, whichever tenant holds it; each timer's scope
    /// is the row's own tenant.
    /// </para>
    /// <para>
    /// <b>Lock order is the write path's</b>: the page's rows first (the update pre-image lock), the timer rows second. A write
    /// that moves a row's due while the page holds it waits, then arms from what the re-plan left.
    /// </para>
    /// </remarks>
    public async Task<Guid?> ReplanTimersAsync(
        string entity, PolicyDecision decision, AlvoContext caller, Guid? after, int limit, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        using var instant = AlvoFlow.EnsureInstant(_time);

        using var db = ContextFor(unit: null);
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var now = WriteInstantNow();
        await using var transaction = await BeginAsync(db, unit: null, cancellationToken);
        var rows = await ReplanPageAsync(db, schema, decision, caller, after, limit, cancellationToken);
        await ReplanRowsAsync(db, transaction, schema, decision, caller, rows, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return rows.Count == limit ? (Guid)rows[^1][AlvoDataContext.IdColumn] : null;
    }

    private async Task<List<Dictionary<string, object>>> ReplanPageAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext caller, Guid? after, int limit,
        CancellationToken cancellationToken)
    {
        var options = new ReadStatementComposer.ReadStatementOptions
        {
            Anchor = after is { } anchor ? new KeysetAnchor([], [], anchor) : null,
            Limit = limit,
            Unmasked = true,
            LockFor = PreImageMutation.Update,
        };

        return await PageAsync(db, schema, decision, caller, options, cancellationToken);
    }

    private async Task ReplanRowsAsync(
        AlvoDataContext db, IDbContextTransaction transaction, EntitySchema schema, PolicyDecision decision, AlvoContext caller,
        List<Dictionary<string, object>> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (connection, inner) = ConnectionOf(db, transaction);
        var keys = rows.SelectMany(row => _lifecycle.TimerKeys(decision, schema.Name, (Guid)row[AlvoDataContext.IdColumn])).ToList();
        var armed = await TimerWrites.ArmedAsync(connection, inner, _timerTable, keys, cancellationToken);
        foreach (var row in rows)
        {
            var replan = new EntityTimerReplan(schema.Name, Unmasked(row), armed, caller, now);
            foreach (var change in _lifecycle.ReplanTimers(decision, replan))
            {
                await WriteTimerChangeAsync(connection, inner, change, cancellationToken);
            }
        }
    }

    private async Task<int> WriteTimerChangeAsync(DbConnection connection, DbTransaction transaction, TimerChange change, CancellationToken cancellationToken) =>
        change.Arm is { } timer
            ? await TimerWrites.UpsertAsync(connection, transaction, _timerTable, timer, Guid.CreateVersion7(_time.GetUtcNow()), cancellationToken)
            : await TimerWrites.CancelAsync(connection, transaction, _timerTable, change.Scope, change.Key, cancellationToken);
}
