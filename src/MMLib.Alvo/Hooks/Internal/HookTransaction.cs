using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The write's transaction as one hook run sees it (Rulings 2, 10, 11, 35): every call counted against the run's budget and run
/// under its token; a write to a row in a hook stage refused as a self-write; <see cref="AsSystem"/> logged and metered. A budget
/// or self-write refusal is recorded on the run and poisons the unit, so catching it changes nothing.
/// </summary>
internal sealed class HookTransaction(IAlvoTransaction inner, HookRun run, BoundWriteHook hook, ILogger logger) : IAlvoTransaction, IAlvoSessionSchema
{
    public AlvoContext Context => inner.Context;

    public EntitySchema? EntityOf(string entity) => (inner as IAlvoSessionSchema)?.EntityOf(entity);

    public Task<AlvoPage> QueryAsync(AlvoQuery query, CancellationToken cancellationToken = default) =>
        ReadAsync(token => inner.QueryAsync(query, token), cancellationToken);

    public Task<AlvoRecord?> GetAsync(string entity, Guid id, CancellationToken cancellationToken = default) =>
        ReadAsync(token => inner.GetAsync(entity, id, token), cancellationToken);

    public Task<AlvoRecord> CreateAsync(string entity, IReadOnlyDictionary<string, object?> values, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [], token => inner.CreateAsync(entity, values, token), cancellationToken);

    public Task<AlvoRecord> UpdateAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], token => inner.UpdateAsync(entity, id, values, precondition, token), cancellationToken);

    public Task<AlvoReplaceResult> ReplaceAsync(
        string entity, Guid id, IReadOnlyDictionary<string, object?> values, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], token => inner.ReplaceAsync(entity, id, values, precondition, token), cancellationToken);

    public Task DeleteAsync(string entity, Guid id, AlvoPrecondition? precondition = null, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [id], async token =>
        {
            await inner.DeleteAsync(entity, id, precondition, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public Task<AlvoBatchResult> CreateManyAsync(
        string entity, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [], token => inner.CreateManyAsync(entity, rows, token), cancellationToken);

    public Task<AlvoBatchResult> UpdateManyAsync(string entity, IReadOnlyList<AlvoRowPatch> rows, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, [.. rows.Select(row => row.Id)], token => inner.UpdateManyAsync(entity, rows, token), cancellationToken);

    public Task<AlvoBatchResult> DeleteManyAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default) =>
        WriteAsync(entity, ids, token => inner.DeleteManyAsync(entity, ids, token), cancellationToken);

    public Task PublishAsync(string type, string subject, IReadOnlyDictionary<string, object?>? data, CancellationToken cancellationToken = default) =>
        ReadAsync(async token =>
        {
            await inner.PublishAsync(type, subject, data, token).ConfigureAwait(false);
            return true;
        }, cancellationToken);

    public IAlvoTransaction AsSystem()
    {
        HookTelemetry.SystemAccess(hook.Id);
        var caller = run.Invocation.Caller;
        HookLog.ExtensionSystemAccess(
            logger, hook.Id, caller.Tenant?.Value.ToString() ?? "-", run.Invocation.Entity, run.Invocation.Operation.ToString().ToLowerInvariant(),
            AlvoIdempotency.IdentityOf(caller), AlvoEventProvenance.Effective(caller).CorrelationId ?? "-");

        return new HookTransaction(inner.AsSystem(), run, hook, logger);
    }

    private Task<T> ReadAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        Count();
        return CallAsync(call, cancellationToken);
    }

    private Task<T> WriteAsync<T>(string entity, IReadOnlyList<Guid> rows, Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (rows.Any(row => HeldRows.Holds(entity, row)))
        {
            throw Poisoned(HookFailedException.SelfWrite);
        }

        Count();
        run.MarkWrote();
        return CallAsync(call, cancellationToken);
    }

    private void Count()
    {
        if (run.Count() > hook.Registration.Settings.MaxOperations)
        {
            Poisoned(HookFailedException.Budget);
            throw AlvoBudgetExceededException.For(hook.Id, hook.Registration.Settings.MaxOperations);
        }
    }

    private HookFailedException Poisoned(string reason)
    {
        var failure = new HookFailedException(hook.Id, reason);
        if (run.Fail(failure))
        {
            HookLog.HookFailed(logger, hook.Id, reason, "-");
        }

        AlvoFlow.Unit?.Poison(failure);
        return failure;
    }

    private async Task<T> CallAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled || cancellationToken == run.Token)
        {
            return await call(run.Token).ConfigureAwait(false);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(run.Token, cancellationToken);
        return await call(linked.Token).ConfigureAwait(false);
    }
}
