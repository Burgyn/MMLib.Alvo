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
    private bool HostHooksRun(string entity, AlvoWriteOperation operation) =>
        FlowUnit is { SuppressesHostHooks: false } && _extensions.RunsInTransaction(entity, operation);

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

        return patch.Count == 0 ? declared : JudgedPatch(db, schema, decision, context, declared, patch);
    }

    private Dictionary<string, object> JudgedPatch(
        AlvoDataContext db, EntitySchema schema, PolicyDecision decision, AlvoContext context, Dictionary<string, object> candidate,
        IReadOnlyDictionary<string, object?> patch)
    {
        EnsureHookMayWrite(patch, decision);
        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);
        EnsureWriteAllowed(decision, Unmasked(patched), previous: null, context);

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

        if (WritePayloadGuard.ReadOnlyRefusal(patch, decision.ReadOnlyFields) is { } frozen)
        {
            return (null, Forbidden(index, frozen));
        }

        var patched = Patched(db.Rows(schema.Name).EntityType, candidate, patch);

        return WriteRefusal(decision, Unmasked(patched), previous: null, context) is { } refusal ? (null, Forbidden(index, refusal)) : (patched, null);
    }

    /// <summary>
    /// The read-only half of the caller's payload guard over a hook's patch (Ruling 48): a field the policy makes read-only for
    /// this caller is refused exactly as the caller's own payload would be — the same 403 and message. Fields frozen only by a
    /// rule's <c>WITH CHECK</c> are judged by stage 5.
    /// </summary>
    private static void EnsureHookMayWrite(IReadOnlyDictionary<string, object?> patch, PolicyDecision decision)
    {
        if (WritePayloadGuard.ReadOnlyRefusal(patch, decision.ReadOnlyFields) is { } refusal)
        {
            throw new AlvoAuthorizationException(refusal);
        }
    }

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
        if (!HostHooksRun(schema.Name, operation))
        {
            return stored;
        }

        var writer = await _extensions.AfterWriteAsync(
            Invocation(schema, operation, context, now) with { Old = preImage, Saved = Unmasked(stored) }, cancellationToken).ConfigureAwait(false);

        return writer is null
            ? stored
            : await SingleAsync(db, schema, decision, context, RowIdOf(stored), lockFor: null, cancellationToken, unmasked: true).ConfigureAwait(false)
                ?? throw Vanished(writer);
    }

    /// <summary>
    /// The row a hook's write made disappear from the write's own read (Ruling 45): the write fails and its unit is poisoned —
    /// never an event or an echo of a row that no longer exists.
    /// </summary>
    private static HookFailedException Vanished(string writer)
    {
        var failure = new HookFailedException(writer, HookFailedException.RowVanished);
        FlowUnit?.Poison(failure);

        return failure;
    }

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

    /// <summary>The idempotency table, ensured before a keyed write opens its own unit — no DDL ever runs inside one.</summary>
    private async Task EnsureIdempotencyTableOutsideAsync(CancellationToken cancellationToken)
    {
        using var db = _contexts.Create();
        await EnsureIdempotencyTableAsync(db, cancellationToken).ConfigureAwait(false);
    }
}
