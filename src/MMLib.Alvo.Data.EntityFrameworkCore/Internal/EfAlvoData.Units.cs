using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;

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
        AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes, CancellationToken cancellationToken)
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
            return await BegunAsync(root, context, options, scopes, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await root.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<WriteUnit> BegunAsync(
        AlvoDataContext root, AlvoContext context, AlvoTransactionOptions options, IServiceScopeFactory scopes,
        CancellationToken cancellationToken)
    {
        var turn = await WriteTurnAsync(root, cancellationToken).ConfigureAwait(false);
        try
        {
            var transaction = await root.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var parts = new WriteUnitParts(root, root.Database.GetDbConnection(), transaction, turn, scopes.CreateAsyncScope());

            return new WriteUnit(parts, context, UnitProvenance(context), options.Timeout, _time, cancellationToken);
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
                    entity, context, idempotency: null,
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
                    entity, context, idempotency: null,
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
                    entity, context, idempotency: null,
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

    /// <summary>
    /// <paramref name="context"/> one hop deeper in its causation chain — the context of a write made under another write's
    /// stage (HX-09's hooks) — refused, and the unit failed, past the limit.
    /// </summary>
    /// <exception cref="AlvoChainDepthExceededException">The deeper context is past the limit; the unit is poisoned.</exception>
    internal AlvoContext Deeper(WriteUnit unit, AlvoContext context)
    {
        var deeper = context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };
        try
        {
            _depthLimit.EnsureWithin(deeper);
        }
        catch (AlvoChainDepthExceededException refusal)
        {
            unit.Poison(refusal);
            throw;
        }

        return deeper;
    }

    private async Task<bool> PublishedAsync(
        WriteUnit unit, string type, string subject, IReadOnlyDictionary<string, object?>? data, AlvoContext context,
        CancellationToken cancellationToken)
    {
        _depthLimit.EnsureWithin(context);
        var custom = AlvoCustomEnvelope.Build(type, subject, data, context, _time.GetUtcNow());
        using var db = ContextFor(unit);
        await using var transaction = await BeginAsync(db, unit, cancellationToken).ConfigureAwait(false);
        await OutboxTable.InsertAsync(unit.Connection, transaction.GetDbTransaction(), _outboxTable, custom.Envelope, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return true;
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
