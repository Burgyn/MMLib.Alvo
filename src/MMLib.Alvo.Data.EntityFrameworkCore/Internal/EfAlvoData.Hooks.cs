using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using MMLib.Alvo.Data.EntityFrameworkCore.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Data.EntityFrameworkCore;

/// <summary>
/// Where a host's write hooks run in the data port (spec 2026-10-14 §4.1): before-write after the declarative before-hooks,
/// its patch judged by <c>WITH CHECK</c> and the tenant scope again (stages 4–5); after-write after the row and its rollups,
/// the stored row read again when a hook wrote (stages 7–7b). The validate stage and its pre-reads are the file's second half.
/// </summary>
/// <remarks>
/// Host hooks run only inside a unit — a <c>TransactAsync</c> unit, or the write's own (Ruling 1) — read off the flow, as
/// <see cref="Hooks"/> is: a write inside a unit only ever runs on that unit's flow.
/// </remarks>
internal sealed partial class EfAlvoData
{
    /// <summary>Gets the unit on the flow; <see langword="null"/> for a top-level write no host hook runs on.</summary>
    private static WriteUnit? FlowUnit => AlvoFlow.Unit as WriteUnit;

    /// <summary>Runs <paramref name="write"/> in a unit of its own when a host in-transaction hook runs on it; as always otherwise.</summary>
    private Task<T> HookedAsync<T>(
        string entity, AlvoWriteOperation operation, AlvoContext context, Func<WriteUnit?, CancellationToken, Task<T>> write,
        CancellationToken cancellationToken) =>
        _extensions.RunsInTransaction(entity, operation) ? OwnUnitAsync(context, write, cancellationToken) : write(null, cancellationToken);

    /// <summary>Whether host hooks run now: registered, inside a unit, and not suppressed by it.</summary>
    private bool HostHooksRun(string entity, AlvoWriteOperation operation) => HookUnit(entity, operation) is not null;

    /// <summary>The unit host hooks run in now; <see langword="null"/> when <see cref="HostHooksRun"/> would answer no.</summary>
    private WriteUnit? HookUnit(string entity, AlvoWriteOperation operation) =>
        FlowUnit is { SuppressesHostHooks: false } unit && _extensions.RunsInTransaction(entity, operation) ? unit : null;

    private WriteInvocation Invocation(EntitySchema schema, AlvoWriteOperation operation, AlvoContext context, DateTimeOffset now)
    {
        var unit = FlowUnit!;

        return new WriteInvocation
        {
            Entity = schema.Name,
            Operation = operation,
            Caller = context,
            Now = now,
            Transaction = HookTransaction(unit, context),
            Services = unit.Services,
        };
    }

    /// <summary>
    /// Stages 4–5 of a create (or a replace that creates), over the candidate the declarative hooks left (stage 3, called
    /// beside it at each create site): host before-write, then the rules again over its patch.
    /// </summary>
    private async Task<Dictionary<string, object>> HostBeforeCreateAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> declared,
        AlvoWriteOperation operation, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, operation))
        {
            return declared;
        }

        var patch = await _extensions.BeforeWriteAsync(Invocation(schema, operation, context, now) with { New = Unmasked(declared) }, cancellationToken).ConfigureAwait(false);

        return patch.Count == 0
            ? declared
            : await JudgedPatchAsync(db, schema, decision, context, declared, patch, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Dictionary<string, object>> JudgedPatchAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        IReadOnlyDictionary<string, object?> patch, CancellationToken cancellationToken)
    {
        EnsureHookMayWrite(patch, decision);
        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);
        await EnsureWriteAllowedAsync(db, schema, decision, Unmasked(patched), previous: null, context, cancellationToken).ConfigureAwait(false);

        return patched;
    }

    /// <summary>A batch row's host before-write: its refusal is the row's (Ruling 4), and so is a rule that refuses its patch.</summary>
    private async Task<(Dictionary<string, object>? Candidate, AlvoRowRefusal? Refusal)> HostBeforeCreateOrRefuseAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, AlvoWriteOperation.Create))
        {
            return (candidate, null);
        }

        IReadOnlyDictionary<string, object?> patch;
        try
        {
            patch = await _extensions.BeforeWriteAsync(
                Invocation(schema, AlvoWriteOperation.Create, context, now) with { New = Unmasked(candidate) }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return (null, row.At(index));
        }

        if (patch.Count == 0)
        {
            return (candidate, null);
        }

        if (HookReadOnlyRefusal(patch, decision) is { } frozen)
        {
            return (null, Forbidden(index, frozen));
        }

        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);

        return await RowRefusalAsync(db, schema, decision, Unmasked(patched), previous: null, context, index, cancellationToken).ConfigureAwait(false) is { } refusal
            ? (null, refusal)
            : (patched, null);
    }

    /// <summary>
    /// The read-only half of the caller's payload guard over a hook's patch (Ruling 48): a field the policy makes read-only for
    /// this caller is refused exactly as the caller's own payload would be — the same 403 and message. Fields frozen only by a
    /// rule's <c>WITH CHECK</c> are judged by stage 5.
    /// </summary>
    private static void EnsureHookMayWrite(IReadOnlyDictionary<string, object?> patch, PolicyDecision decision)
    {
        if (HookReadOnlyRefusal(patch, decision) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

    /// <summary>
    /// The port's read-only refusal of a hook's patch, except that a field the caller may not see is never named (Ruling 17):
    /// the caller never sent it, so naming it would disclose it.
    /// </summary>
    private static string? HookReadOnlyRefusal(IReadOnlyDictionary<string, object?> patch, PolicyDecision decision)
    {
        var refused = patch.Keys.FirstOrDefault(decision.ReadOnlyFields.Contains);

        return refused is null ? null
            : decision.HiddenFields.Contains(refused) ? HiddenFieldRefusal
            : WritePayloadGuard.ReadOnlyRefusal(patch, decision.ReadOnlyFields);
    }

    private const string HiddenFieldRefusal = "A write hook set a field this caller may not write.";

    /// <summary>Stage 4 of an update or a replace of an existing row: the host's patch over the declarative one.</summary>
    private async Task<IReadOnlyDictionary<string, object?>> HostBeforeUpdateAsync(
        EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation, AlvoRecord preImage,
        IReadOnlyDictionary<string, object?> values, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (!HostHooksRun(schema.Name, operation))
        {
            return values;
        }

        var patch = await _extensions.BeforeWriteAsync(
            Invocation(schema, operation, context, now) with { Old = preImage, New = Merge(preImage, values) }, cancellationToken).ConfigureAwait(false);

        if (patch.Count == 0)
        {
            return values;
        }

        EnsureHookMayWrite(patch, decision);
        return Overlaid(values, patch);
    }

    private static Dictionary<string, object?> Overlaid(IReadOnlyDictionary<string, object?> values, IReadOnlyDictionary<string, object?> patch)
    {
        var overlaid = new Dictionary<string, object?>(values, StringComparer.Ordinal);
        foreach (var (field, value) in patch)
        {
            overlaid[field] = value;
        }

        return overlaid;
    }

    /// <summary>A batch row's update stage 4, its refusal the row's.</summary>
    private async Task<(IReadOnlyDictionary<string, object?>? Values, AlvoRowRefusal? Refusal)> HostBeforeUpdateOrRefuseAsync(
        EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoRecord preImage, IReadOnlyDictionary<string, object?> values,
        DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        try
        {
            return (await HostBeforeUpdateAsync(schema, decision, context, AlvoWriteOperation.Update, preImage, values, now, cancellationToken).ConfigureAwait(false), null);
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return (null, row.At(index));
        }
        catch (AlvoAuthorizationException frozen)
        {
            return (null, Forbidden(index, frozen.Message));
        }
    }

    /// <summary>Stages 7–7b: the host's after-write over the stored row, which is read again when a hook wrote.</summary>
    private async Task<Dictionary<string, object>> AfterSavedAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation, AlvoRecord? preImage,
        Dictionary<string, object> stored, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (HookUnit(schema.Name, operation) is not { } unit)
        {
            return stored;
        }

        var writer = await _extensions.AfterWriteAsync(
            Invocation(schema, operation, context, now) with { Old = preImage, Saved = Unmasked(stored) }, cancellationToken).ConfigureAwait(false);

        return writer is null
            ? stored
            : await SingleAsync(db, schema, decision, context, RowIdOf(stored), lockFor: null, cancellationToken, unmasked: true).ConfigureAwait(false)
                ?? throw Vanished(unit, writer);
    }

    /// <summary>
    /// The row a hook's write made disappear from the write's own read (Ruling 45): the write fails and its unit is poisoned —
    /// never an event or an echo of a row that no longer exists. Logged here: no runner saw this failure, and the HTTP
    /// handler answers every hook failure without a second Error line. The unit is a parameter, not a lookup, so no path can
    /// reach this without one to poison and log through.
    /// </summary>
    private static HookFailedException Vanished(WriteUnit unit, string writer)
    {
        var failure = new HookFailedException(writer, HookFailedException.RowVanished);
        unit.Poison(failure);
        RowVanished(unit.Logger(), writer);

        return failure;
    }

    [LoggerMessage(
        Level = LogLevel.Error,
        Message = "Write hook {Extension} failed (row-vanished): after its writes, the write could no longer read back the row it ran on; "
            + "the write was rolled back.")]
    private static partial void RowVanished(ILogger logger, string extension);

    /// <summary>A batch's stage 7 for each written row: the first refusal is the batch's, and rolls every row back (Ruling 4).</summary>
    private async Task<(List<Dictionary<string, object>> Stored, AlvoRowRefusal? Refusal)> AfterSavedRowsAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, AlvoWriteOperation operation,
        IReadOnlyList<(int Index, AlvoRecord? PreImage, Dictionary<string, object> Stored)> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var stored = new List<Dictionary<string, object>>(rows.Count);
        foreach (var (index, preImage, row) in rows)
        {
            try
            {
                stored.Add(await AfterSavedAsync(db, schema, decision, context, operation, preImage, row, now, cancellationToken).ConfigureAwait(false));
            }
            catch (Exception refused) when (refused is IRowRefusal refusal)
            {
                return (stored, refusal.At(index));
            }
        }

        return (stored, null);
    }

    /// <summary>A delete's host before-delete; a delete carries no patch.</summary>
    private async Task HostBeforeDeleteAsync(EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (HostHooksRun(schema.Name, AlvoWriteOperation.Delete))
        {
            _ = await _extensions.BeforeWriteAsync(Invocation(schema, AlvoWriteOperation.Delete, context, now) with { Old = preImage }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A delete's host after-delete, once the row is gone and its rollups recomputed.</summary>
    private async Task HostAfterDeleteAsync(EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (HostHooksRun(schema.Name, AlvoWriteOperation.Delete))
        {
            _ = await _extensions.AfterWriteAsync(Invocation(schema, AlvoWriteOperation.Delete, context, now) with { Old = preImage }, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A batch's after-delete for each removed row: the first refusal is the batch's, and keeps every row (Ruling 4).</summary>
    private async Task<AlvoRowRefusal?> HostAfterDeleteRowsAsync(
        EntitySchema schema, AlvoContext context, IReadOnlyList<(int Index, AlvoRecord PreImage)> rows, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var (index, preImage) in rows)
        {
            try
            {
                await HostAfterDeleteAsync(schema, context, preImage, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception refused) when (refused is IRowRefusal row)
            {
                return row.At(index);
            }
        }

        return null;
    }

    /// <summary>
    /// Every row of a batch held as in a hook stage while its hooks run (Ruling 43), so a hook writing a sibling row is a
    /// self-write; a batch no host hook runs on holds nothing — no hook of it can make a nested write.
    /// </summary>
    private IDisposable? HeldWhileHooksRun(EntitySchema schema, AlvoWriteOperation operation, IEnumerable<Guid> ids) =>
        HostHooksRun(schema.Name, operation) ? HeldRows.HoldAll(schema.Name, ids) : null;

    /// <summary>A batch row's before-delete, its refusal the row's.</summary>
    private async Task<AlvoRowRefusal?> HostBeforeDeleteOrRefuseAsync(
        EntitySchema schema, AlvoContext context, AlvoRecord preImage, DateTimeOffset now, int index, CancellationToken cancellationToken)
    {
        try
        {
            await HostBeforeDeleteAsync(schema, context, preImage, now, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return row.At(index);
        }
    }

    /// <summary>
    /// Stages 0–2 of a create: the key looked up autocommit (Ruling 14), the authorized candidate (stage 1), and the validate hooks —
    /// before the replay loop and before any transaction (C14, C15). The candidate's id is left out: it is minted per attempt.
    /// </summary>
    private async Task ValidateCreateAsync(
        string entity, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoContext context, AlvoIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, AlvoWriteOperation.Create) || await ReplaysAsync(idempotency, context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        using var db = _contexts.Create();
        var now = WriteInstantNow();
        var (_, candidate) = await AuthorizedCandidateAsync(db, entity, values, decision, context, now, cancellationToken).ConfigureAwait(false);
        await _extensions.ValidateAsync(
            Validation(entity, AlvoWriteOperation.Create, context, now) with { New = WithoutId(Unmasked(candidate)) }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stages 0–2 of an update or a replace: the caller's own payload checks first, by the very guard the write runs
    /// (<c>UpdatableEntity</c> or <c>ReplaceableEntity</c>; Ruling 41 — no hook sees a payload the write will refuse), E9's
    /// pre-read — autocommit, unmasked, under the operation's own decision (Ruling 13) — and the validate hooks. A row the pre-read
    /// does not find is judged by <see cref="EnsureCreatableAsync"/> before any hook runs, against <c>createsWith</c> — a replace's
    /// create decision, or <see langword="null"/> for an update, which never creates — and the write's <c>precondition</c>.
    /// </summary>
    private async Task ValidateChangeAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoWriteOperation operation, PolicyDecision decision,
        PolicyDecision? createsWith, AlvoPrecondition? precondition, Func<AlvoDataContext, EntitySchema> guard, AlvoContext context,
        AlvoIdempotency? idempotency, CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, operation) || await ReplaysAsync(idempotency, context, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        using var db = _contexts.Create();
        var schema = guard(db);
        var now = WriteInstantNow();
        var old = await PreReadAsync(db, schema, decision, context, id, cancellationToken).ConfigureAwait(false);
        if (old is null)
        {
            await EnsureCreatableAsync(db, schema, createsWith, precondition, context, id, values, now, cancellationToken).ConfigureAwait(false);
        }

        await _extensions.ValidateAsync(
            Validation(entity, operation, context, now) with { Old = old, New = ValidatedNew(schema, id, old, values, decision, operation) },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A change whose row the pre-read did not find. An update answers 404 before any hook runs: validate never fails open
    /// (Ruling 42). A replace is validated as the create it will be, so it is judged first by the create rule's <c>WITH CHECK</c>
    /// and the tenant scope over the candidate <c>CreatedByReplaceAsync</c> inserts — no outbound call for a create the write
    /// will refuse, exactly as a plain create (Ruling 60). A precondition is checked first, as <c>CreatedByReplaceAsync</c> checks
    /// it: a version names a row that is not there, so it answers 412 whether or not a validate hook is registered (Ruling 61).
    /// </summary>
    private async Task EnsureCreatableAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision? createsWith, AlvoPrecondition? precondition, AlvoContext context, Guid id,
        IReadOnlyDictionary<string, object?> values, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (createsWith is null)
        {
            throw new AlvoRecordNotFoundException();
        }

        AlvoPrecondition.EnsureMatches(precondition, storedVersion: null);
        var candidate = Unmasked(ReplaceCandidate(db, schema, createsWith, context, id, values, now));
        await EnsureWriteAllowedAsync(db, schema, createsWith, candidate, previous: null, context, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AlvoRecord?> PreReadAsync(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Guid id, CancellationToken cancellationToken) =>
        await SingleAsync(db, schema, decision, context, id, lockFor: null, cancellationToken, unmasked: true).ConfigureAwait(false) is { } row
            ? Unmasked(row)
            : null;

    /// <summary>The row a change will store: a replace's whole row (its defaults kept off frozen fields when it replaces), or an update merged over the pre-image.</summary>
    private static AlvoRecord ValidatedNew(
        EntitySchema schema, Guid id, AlvoRecord? old, IReadOnlyDictionary<string, object?> values, PolicyDecision decision, AlvoWriteOperation operation) =>
        operation == AlvoWriteOperation.Replace
            ? new AlvoRecord(WholeRowValues(schema, values, old is null ? null : decision)).With(AlvoManagedColumns.Id, id)
            : Merge(old!, values);

    /// <summary>
    /// A batch's stage 2: every row judged — its payload guard, its rule, its row — and every admitted row validated, once,
    /// before the replay loop and before <c>OneBatchAsync</c> opens its transaction (C16). Every bad row is reported, whichever
    /// check refused it (Ruling 59); any refusal refuses the batch with no transaction opened.
    /// </summary>
    private async Task<IReadOnlyList<AlvoRowRefusal>> ValidateRowsAsync(
        string entity, AlvoWriteOperation operation, int count, Func<AlvoDataContext, EntitySchema, DateTimeOffset, int, Task<RowValidation>> judge,
        AlvoContext context, AlvoIdempotency? idempotency, CancellationToken cancellationToken)
    {
        if (!_extensions.Validates(entity, operation) || await ReplaysAsync(idempotency, context, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        using var db = _contexts.Create();
        var schema = Entity(db, entity) ?? throw new AlvoAuthorizationException(UnknownEntityMessage);
        var now = WriteInstantNow();
        var refusals = new List<AlvoRowRefusal>();
        for (var index = 0; index < count; index++)
        {
            var judged = await judge(db, schema, now, index).ConfigureAwait(false);
            var refused = judged.Refusal ?? await ValidatedRowAsync(judged.Invocation!, index, cancellationToken).ConfigureAwait(false);
            if (refused is not null)
            {
                refusals.Add(refused);
            }
        }

        return refusals;
    }

    /// <summary>One batch row's stage-1 verdict: the invocation its validate hooks see, or the refusal that keeps them from it.</summary>
    private readonly record struct RowValidation(WriteInvocation? Invocation, AlvoRowRefusal? Refusal);

    private async Task<AlvoRowRefusal?> ValidatedRowAsync(WriteInvocation invocation, int index, CancellationToken cancellationToken)
    {
        try
        {
            await _extensions.ValidateAsync(invocation, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception refused) when (refused is IRowRefusal row)
        {
            return row.At(index);
        }
    }

    /// <summary>One create row: its payload and rule refusal, or its validation.</summary>
    private Func<AlvoDataContext, EntitySchema, DateTimeOffset, int, Task<RowValidation>> CreateRowValidation(
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, PolicyDecision decision, AlvoContext context,
        CancellationToken cancellationToken) =>
        async (db, schema, now, index) => await JudgedCandidateAsync(db, schema, decision, context, rows[index], now, index, cancellationToken).ConfigureAwait(false) switch
        {
            ({ } candidate, null) => new RowValidation(
                Validation(schema.Name, AlvoWriteOperation.Create, context, now) with { New = WithoutId(Unmasked(candidate)) }, null),
            (_, var refusal) => new RowValidation(null, refusal),
        };

    /// <summary>
    /// One update row: a repeat of an earlier row's id, its payload refusal, the row unavailable to this caller (fail closed,
    /// Ruling 42), or its validation. A repeat is that row's own refusal, never the batch's, so every other row is still judged
    /// (Ruling 59) and no id is validated twice. The repeats are found on the first row judged, so a batch no validate hook runs on
    /// never looks for them here.
    /// </summary>
    private Func<AlvoDataContext, EntitySchema, DateTimeOffset, int, Task<RowValidation>> UpdateRowValidation(
        IReadOnlyList<AlvoRowPatch> rows, PolicyDecision decision, AlvoContext context, CancellationToken cancellationToken)
    {
        var repeats = new Lazy<Dictionary<int, AlvoRowRefusal>>(() => BatchWrite.RepeatedRows(rows, row => row.Id, NamedTwice).ToDictionary(refusal => refusal.Index));

        return async (db, schema, now, index) =>
        {
            if (repeats.Value.TryGetValue(index, out var repeat))
            {
                return new RowValidation(null, repeat);
            }

            if (WritePayloadGuard.PayloadRefusal(rows[index].Values, schema, decision, isUpdate: true) is { } unwritable)
            {
                return new RowValidation(null, Forbidden(index, unwritable));
            }

            return await PreReadAsync(db, schema, decision, context, rows[index].Id, cancellationToken).ConfigureAwait(false) is { } old
                ? new RowValidation(Validation(schema.Name, AlvoWriteOperation.Update, context, now) with { Old = old, New = Merge(old, rows[index].Values) }, null)
                : new RowValidation(null, Unavailable(index));
        };
    }

    private static WriteInvocation Validation(string entity, AlvoWriteOperation operation, AlvoContext context, DateTimeOffset now) =>
        new() { Entity = entity, Operation = operation, Caller = context, Now = now };

    private static AlvoRecord WithoutId(AlvoRecord record) =>
        new(record.Values.Where(entry => entry.Key != AlvoManagedColumns.Id).ToDictionary(StringComparer.Ordinal));

    /// <summary>Stage 0: whether the key is already recorded for this caller — read autocommit, before any transaction (Ruling 14).</summary>
    private async Task<bool> ReplaysAsync(AlvoIdempotency? idempotency, AlvoContext context, CancellationToken cancellationToken)
    {
        if (idempotency is not { } token)
        {
            return false;
        }

        await EnsureIdempotencyTableOutsideAsync(cancellationToken).ConfigureAwait(false);
        using var db = _contexts.Create();
        var connection = db.Database.GetDbConnection();
        await RelationalSqlBatch.OpenAsync(connection, cancellationToken).ConfigureAwait(false);

        return await IdempotencyTable.FindAsync(connection, transaction: null, _idempotencyTable, token.Key, AlvoIdempotency.IdentityOf(context), cancellationToken)
            .ConfigureAwait(false) is not null;
    }

    /// <summary>
    /// The idempotency table, ensured before a keyed write opens its own unit — no DDL ever runs inside one — and once per
    /// process: concurrent first callers share one run (<see cref="OnceDdl"/>, Ruling 47), so two first writes of one
    /// process never race each other on the engine's catalog. Other processes still can; <see cref="IfNotExistsDdl"/>
    /// answers that.
    /// </summary>
    private Task EnsureIdempotencyTableOutsideAsync(CancellationToken cancellationToken) =>
        _idempotencyTableEnsured ? Task.CompletedTask : _idempotencyDdl.EnsureAsync(cancellationToken);

    /// <summary><see cref="EnsureIdempotencyTableAsync"/> on a context of its own, which no write transaction holds.</summary>
    private async Task EnsureIdempotencyTableOnOwnContextAsync(CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        await EnsureIdempotencyTableAsync(db, cancellationToken).ConfigureAwait(false);
    }
}
