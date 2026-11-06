using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;
using System.Data.Common;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// The data port's transaction units (spec 2026-10-14 §4.5): opening one, and running every read, write, batch and
/// publish of <c>IAlvoTransaction</c> inside it.
/// </summary>
/// <remarks>
/// A partial file of its own because it is the one place the data port reaches a unit's connection: it is allow-listed in
/// <c>ChangeTrackerReachTests</c> beside <c>EfAlvoData.cs</c> for exactly that.
/// </remarks>
internal sealed partial class EfAlvoData
{
    /// <summary>Opens one unit as <paramref name="context"/>: the outbox ensured, the write turn taken, the transaction begun.</summary>
    /// <param name="context">The caller every call of the unit runs as.</param>
    /// <param name="options">The checked options.</param>
    /// <param name="scopes">Where the unit's DI scope comes from.</param>
    /// <param name="cancellationToken">The caller's token, which every call of the unit also honours.</param>
    /// <param name="owned">Whether the unit is opened for one top-level write a host hook runs on (Ruling 1).</param>
    /// <returns>The open unit; the caller disposes it.</returns>
    /// <exception cref="AlvoChainDepthExceededException">The caller is deeper in one causation chain than the limit allows.</exception>
    /// <remarks>
    /// <para>
    /// The connection is opened <b>through EF</b> first, so the provider's connection interceptors run on it — SQLite's
    /// <c>case_sensitive_like</c> among them. <c>EnsureOutboxTableAsync</c> opens a closed connection raw, which runs no
    /// interceptor, and the unit's connection is reused by every call: opened raw, the first unit of a process would match
    /// <c>like</c> and the literal text tests case-insensitively on SQLite (Ruling 32).
    /// </para>
    /// <para>
    /// The outbox table is ensured here, before <c>BEGIN</c>, so no write inside the unit ever runs DDL on its connection —
    /// which SQLite would refuse inside a pending transaction.
    /// </para>
    /// </remarks>
    internal async Task<WriteUnit> OpenUnitAsync(
        AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes, CancellationToken cancellationToken,
        bool owned = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        _depthLimit.EnsureWithin(context);

        var root = _contexts.Create();
        try
        {
            await root.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await EnsureOutboxTableAsync(root, cancellationToken).ConfigureAwait(false);
            return await BegunAsync(root, context, options, owned, scopes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await root.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WriteUnit> BegunAsync(
        AlvoDataContext root, AlvoContext context, AlvoTransactionOptions options, bool owned, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var turn = await WriteTurnAsync(root, cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await root.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var parts = new WriteUnitParts(root, root.Database.GetDbConnection(), transaction, turn, scopes.CreateAsyncScope());

            return new WriteUnit(parts, context, UnitProvenance(context), options, owned, _time, cancellationToken);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    /// <summary>The caller's effective provenance — with a correlation id of the unit's own when it carries none, so every event of one unit shares one.</summary>
    private static AlvoProvenance UnitProvenance(AlvoContext context)
    {
        var effective = AlvoEventProvenance.Effective(context);

        return effective.CorrelationId is null
            ? effective with { CorrelationId = AlvoEventProvenance.CorrelationIdOf(Guid.CreateVersion7()) }
            : effective;
    }

    /// <summary>The declarative hook pipeline this write runs: its unit's, resolved from the unit's scope, or the driver's own outside a unit.</summary>
    /// <remarks>
    /// Read from the flow rather than threaded through every write method: a write inside a unit only ever runs on that
    /// unit's flow, and outside one the data port refuses nothing reaches this with a unit open.
    /// </remarks>
    private IBeforeHookRunner Hooks => AlvoFlow.Unit is WriteUnit unit ? unit.Hooks : _hooks;

    /// <summary>A context for one data operation — on the unit's connection when it runs inside <paramref name="unit"/>.</summary>
    /// <param name="unit">The unit the operation runs in, or <see langword="null"/> for a top-level one.</param>
    private AlvoDataContext ContextFor(WriteUnit? unit)
    {
        var db = _contexts.Create();
        if (unit is not null)
        {
            db.Database.SetDbConnection(unit.Connection, contextOwnsConnection: false);
        }

        return db;
    }

    /// <summary>
    /// The one place a write's transaction begins: a top-level write takes the turn and its own transaction, exactly as before;
    /// a write inside a unit joins the unit's transaction under a savepoint of its own.
    /// </summary>
    /// <param name="db">The write's context.</param>
    /// <param name="unit">The unit the write runs in, or <see langword="null"/> for a top-level write.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    private async Task<IDbContextTransaction> BeginAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken cancellationToken)
    {
        if (unit is not null)
        {
            return await UnitSavepoint.JoinAsync(db, unit, cancellationToken).ConfigureAwait(false);
        }

        var turn = await WriteTurnAsync(db, cancellationToken).ConfigureAwait(false);
        try
        {
            return new TurnHeldTransaction(await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false), turn);
        }
        catch
        {
            turn.Dispose();
            throw;
        }
    }

    /// <summary>A read inside a unit joins its transaction — so it sees the unit's writes — under a savepoint; a top-level read joins nothing.</summary>
    /// <param name="db">The read's context.</param>
    /// <param name="unit">The unit the read runs in, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    private static async Task<IDbContextTransaction?> ReadScopeAsync(AlvoDataContext db, WriteUnit? unit, CancellationToken cancellationToken) =>
        unit is null ? null : await UnitSavepoint.JoinAsync(db, unit, cancellationToken).ConfigureAwait(false);

    /// <summary>Releases a read's savepoint once the read succeeded, so the unit's savepoint stack does not grow with every read (Ruling 34).</summary>
    /// <param name="joined">The read's savepoint, or <see langword="null"/> for a top-level read.</param>
    /// <param name="cancellationToken">A token to cancel the release.</param>
    private static async Task ReleaseAsync(IDbContextTransaction? joined, CancellationToken cancellationToken)
    {
        if (joined is not null)
        {
            await joined.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One page of a query inside <paramref name="unit"/>.</summary>
    internal Task<AlvoPage> QueryInUnitAsync(WriteUnit unit, AlvoQuery query, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return unit.RunAsync(token => QueriedAsync(query, context, unit, token), cancellationToken);
    }

    /// <summary>One record inside <paramref name="unit"/>.</summary>
    internal Task<AlvoRecord?> GetInUnitAsync(WriteUnit unit, string entity, Guid id, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return unit.RunAsync(token => GotAsync(entity, id, context, unit, token), cancellationToken);
    }

    /// <summary>One create inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoRecord> CreateInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyDictionary<string, object?> values, AlvoContext context, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => CreatedAsync(entity, values, Decided(entity, DataOperation.Create, context), context, unit, token), cancellationToken);
    }

    /// <summary>One update inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoRecord> UpdateInUnitAsync(
        WriteUnit unit, string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => UpdatedAsync(entity, id, values, Decided(entity, DataOperation.Update, context), context, precondition, unit, token),
            cancellationToken);
    }

    /// <summary>One create-or-replace inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task<AlvoReplaceResult> ReplaceInUnitAsync(
        WriteUnit unit, string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoContext context,
        AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        EnsureUnitWrite(entity, values);
        return unit.RunAsync(
            token => ReplacedAsync(entity, id, values, Branches(entity, context), context, precondition, unit, token), cancellationToken);
    }

    /// <summary>One delete inside <paramref name="unit"/>, as <paramref name="context"/>, under its own savepoint.</summary>
    internal Task DeleteInUnitAsync(
        WriteUnit unit, string entity, Guid id, AlvoContext context, AlvoPrecondition? precondition, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        return unit.RunAsync(
            async token =>
            {
                await ErasedAsync(entity, id, Decided(entity, DataOperation.Delete, context), context, precondition, unit, token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    /// <summary>One create batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> CreateManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, AlvoContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Create, rows.Count, context, idempotency: null);
                return BatchAsync(
                    entity, AlvoWriteOperation.Create, context, idempotency: null,
                    (db, transaction, schema, now) => CreatedRowsAsync(db, transaction, schema, decision, context, rows, now, token),
                    producesRows: true, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One update batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> UpdateManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<AlvoRowPatch> rows, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Update, rows.Count, context, idempotency: null);
                return BatchAsync(
                    entity, AlvoWriteOperation.Update, context, idempotency: null,
                    (db, transaction, schema, now) => UpdatedRowsAsync(db, transaction, schema, decision, context, rows, now, token),
                    producesRows: true, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One delete batch inside <paramref name="unit"/>: all its rows or none, under its own savepoint.</summary>
    internal Task<AlvoBatchResult> DeleteManyInUnitAsync(
        WriteUnit unit, string entity, IReadOnlyList<Guid> ids, AlvoContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        return unit.RunAsync(
            token =>
            {
                var decision = BatchDecision(entity, DataOperation.Delete, ids.Count, context, idempotency: null);
                return BatchAsync(
                    entity, AlvoWriteOperation.Delete, context, idempotency: null,
                    (db, transaction, schema, now) => RemovedRowsAsync(db, transaction, schema, decision, context, ids, now, token),
                    producesRows: false, unit, token);
            },
            cancellationToken);
    }

    /// <summary>One custom event written into <paramref name="unit"/>'s transaction: it commits with the unit, or not at all (spec 2026-10-14 X13).</summary>
    internal Task PublishInUnitAsync(
        WriteUnit unit, string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        CancellationToken cancellationToken)
    {
        AlvoCustomEnvelope.Ensure(type, subject, data);
        return unit.RunAsync(token => PublishedAsync(unit, type, subject, data, context, token), cancellationToken);
    }

    /// <summary>One framework event (a command's completion) written into <paramref name="unit"/>'s transaction.</summary>
    internal Task PublishFrameworkInUnitAsync(WriteUnit unit, AlvoFrameworkEnvelope @event, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return unit.RunAsync(
            token => InUnitAsync(
                unit, (connection, transaction, ct) => OutboxTable.InsertAsync(connection, transaction, _outboxTable, @event.Envelope, ct), token),
            cancellationToken);
    }

    /// <summary>
    /// One timer-store write inside <paramref name="unit"/>'s transaction, under a savepoint of its own: the one store write
    /// that joins the unit on the flow rather than refusing it, so a unit that rolls back takes the timer with it.
    /// </summary>
    internal Task<T> InFlowUnitAsync<T>(
        WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(write);
        return unit.RunAsync(token => WrittenInUnitAsync(unit, write, token), cancellationToken);
    }

    private async Task<T> WrittenInUnitAsync<T>(
        WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        var result = default(T)!;
        await InUnitAsync(
            unit, async (connection, transaction, ct) => result = await write(connection, transaction, ct).ConfigureAwait(false), cancellationToken)
            .ConfigureAwait(false);

        return result;
    }

    /// <summary>
    /// A command key's commit mark written into <paramref name="unit"/>'s transaction, only while the run holds its claim;
    /// otherwise the unit is poisoned and <see cref="CommandClaimLostException"/> thrown (Ruling 36).
    /// </summary>
    internal Task CommitCommandInUnitAsync(WriteUnit unit, CommandKey key, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        CommandLedgerTable.Claimed(key);
        return unit.RunAsync(
            token => InUnitAsync(
                unit, (connection, transaction, ct) => CommittedOrLostAsync(unit, connection, transaction, key, now, ct), token),
            cancellationToken);
    }

    /// <summary>Marks <paramref name="key"/> committed, or poisons the unit when its claim was taken over.</summary>
    /// <remarks>
    /// A lost claim <b>poisons</b> the unit: even a caller that caught the exception cannot commit the run's writes beside
    /// the run that took the key over.
    /// </remarks>
    private async Task CommittedOrLostAsync(
        WriteUnit unit, DbConnection connection, DbTransaction transaction, CommandKey key, DateTimeOffset now, CancellationToken ct)
    {
        if (await CommandLedgerTable.CommitAsync(connection, transaction, _commandTable, key, now, ct).ConfigureAwait(false) == 1)
        {
            return;
        }

        var lost = new CommandClaimLostException();
        unit.Poison(lost);
        throw lost;
    }

    /// <summary>
    /// One raw write inside <paramref name="unit"/>, under a savepoint of its own — the shape every outbox insert of a unit
    /// takes.
    /// </summary>
    private async Task<bool> InUnitAsync(
        WriteUnit unit, Func<DbConnection, DbTransaction, CancellationToken, Task> write, CancellationToken cancellationToken)
    {
        using var db = ContextFor(unit);
        await using var transaction = await BeginAsync(db, unit, cancellationToken).ConfigureAwait(false);
        await write(unit.Connection, transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
    }

    /// <summary>
    /// <paramref name="context"/> one hop deeper in its causation chain, unchecked: each write and publish through a unit checks
    /// the limit inside its own call, where the refusal poisons the unit (Ruling 9).
    /// </summary>
    internal static AlvoContext OneHopDeeper(AlvoContext context) =>
        context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };

    /// <summary>
    /// Binds this store's clock as the flow's instant unless one is bound (HX-11 Ruling 1): <c>TransactAsync</c>'s entry, so
    /// every write of a unit reads one <c>now()</c>.
    /// </summary>
    /// <returns>A scope that unbinds it, or a no-op when an instant was already bound.</returns>
    internal IDisposable EnsureInstant() => AlvoFlow.EnsureInstant(_time);

    /// <summary>What a host hook of a write in <paramref name="unit"/> receives as <c>write.Transaction</c>: the unit, as the writer, one hop deeper.</summary>
    internal IAlvoTransaction HookTransaction(WriteUnit unit, AlvoContext context) => new EfAlvoTransaction(this, unit, OneHopDeeper(context));

    /// <summary>Runs one top-level write in a unit of its own, on the flow, and commits it (Ruling 1).</summary>
    private async Task<T> OwnUnitAsync<T>(
        AlvoContext context, Func<WriteUnit?, CancellationToken, Task<T>> write, CancellationToken cancellationToken)
    {
        var unit = await OpenUnitAsync(context, AlvoTransactionOptions.Validated(null, context), Scopes, cancellationToken, owned: true)
            .ConfigureAwait(false);
        try
        {
            return await UnitRun.CommittedAsync(unit, () => unit.RunAsync(token => write(unit, token), cancellationToken)).ConfigureAwait(false);
        }
        finally
        {
            await ReleasedAndFlushedAsync(unit).ConfigureAwait(false);
        }
    }

    private IServiceScopeFactory Scopes => _scopes ?? throw new InvalidOperationException(
        "A write a host hook runs on needs the host's IServiceScopeFactory, and this data port was built without one.");

    /// <summary>Releases <paramref name="unit"/> and then records its rejections — even when the release throws (Ruling 8: whatever the outcome).</summary>
    internal async Task ReleasedAndFlushedAsync(WriteUnit unit)
    {
        try
        {
            await unit.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            await FlushRejectionsAsync(unit).ConfigureAwait(false);
        }
    }

    /// <summary>Records the rejections a unit remembered, after it released its write turn and left the flow (Ruling 8).</summary>
    internal Task FlushRejectionsAsync(WriteUnit unit) =>
        _rejections is null || unit.Rejections.Count == 0 ? Task.CompletedTask : _rejections.FlushAsync(unit.Rejections);

    /// <summary>
    /// The one audit row of a unit that suppresses host hooks, written in the unit's own transaction — a rolled-back unit
    /// suppressed nothing durably (Ruling 30).
    /// </summary>
    internal async Task<DeliveryOperationEntry> AuditSuppressionAsync(WriteUnit unit, string reason, CancellationToken cancellationToken)
    {
        var entry = new DeliveryOperationEntry
        {
            Id = Guid.CreateVersion7(),
            At = _time.GetUtcNow(),
            Actor = "system",
            Operation = "suppress-host-hooks",
            Target = $"unit:{unit.Provenance.CorrelationId}",
            Reason = reason,
            Affected = 1,
        };
        await unit.RunAsync(
            token => InUnitAsync(unit, (connection, transaction, ct) => LoggedAsync(connection, transaction, entry, ct), token),
            cancellationToken).ConfigureAwait(false);

        return entry;
    }

    private async Task LoggedAsync(DbConnection connection, DbTransaction transaction, DeliveryOperationEntry entry, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = DeliveryOperationsTable.InsertLogSql(_operationsLog);
        DeliveryOperationsTable.BindLogEntry(command, entry);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private Task<bool> PublishedAsync(
        WriteUnit unit, string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        CancellationToken cancellationToken)
    {
        _depthLimit.EnsureWithin(context);
        var custom = AlvoCustomEnvelope.Build(type, subject, data, context, _time.GetUtcNow());

        return InUnitAsync(
            unit,
            (connection, transaction, ct) => OutboxTable.InsertAsync(connection, transaction, _outboxTable, custom.Envelope, ct),
            cancellationToken);
    }

    private PolicyDecision Decided(string entity, DataOperation operation, AlvoContext context)
    {
        _depthLimit.EnsureWithin(context);
        return Resolve(entity, operation, context);
    }

    private ReplaceDecisions Branches(string entity, AlvoContext context)
    {
        _depthLimit.EnsureWithin(context);
        return new ReplaceDecisions(Resolve(entity, DataOperation.Create, context), Resolve(entity, DataOperation.Update, context));
    }

    private static void EnsureUnitWrite(string entity, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(values);
    }
}
