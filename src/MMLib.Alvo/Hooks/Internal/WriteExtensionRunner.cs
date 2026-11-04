using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Schema;
using System.Diagnostics;

namespace MMLib.Alvo.Hooks.Internal;

/// <summary>
/// The core's <see cref="IWriteExtensionRunner"/> (spec 2026-10-14 §4.4): the bound hooks of one entity and stage, in order,
/// each whose <c>when</c> holds run once and awaited to its end (Ruling 11), and turned into a patch, a rejection, or a failure
/// that has already poisoned the unit on the flow.
/// </summary>
/// <remarks>
/// <b>Nothing it holds reaches the network:</b> its own dependencies are the catalog, the binder, the clock, a logger and the
/// root provider it resolves the session factory, the delivery store and the rejection log from — the architecture fact over
/// <c>WriteExtensionRunner</c>'s constructor (§4.4.5) pins that list.
/// </remarks>
internal sealed class WriteExtensionRunner(
    ExtensionRegistry registry, ExtensionBinder binder, IPolicyCatalogProvider catalogs, IServiceProvider services, TimeProvider time,
    ILogger<WriteExtensionRunner> logger) : IWriteExtensionRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _none = new Dictionary<string, object?>(StringComparer.Ordinal);

    public bool Validates(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: false);

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: true);

    public async Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var hooks = Selected(invocation, WriteHookStage.Validate);
        if (hooks.Count == 0)
        {
            return;
        }

        var disabled = await DisabledAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var scoped = invocation with { Services = scope.ServiceProvider };
        foreach (var hook in hooks.Where(hook => !disabled.Contains(hook.Id) && Fires(hook, scoped, WriteHookStage.Validate)))
        {
            await RunAsync(hook, scoped, WriteHookStage.Validate, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var stage = invocation.Operation == AlvoWriteOperation.Delete ? WriteHookStage.BeforeDelete : WriteHookStage.BeforeWrite;
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = invocation;
        foreach (var hook in Selected(invocation, stage))
        {
            if (!Fires(hook, current, stage))
            {
                continue;
            }

            var sets = (await RunAsync(hook, current, stage, cancellationToken).ConfigureAwait(false)).Sets;
            foreach (var (field, value) in sets)
            {
                patch[field] = value;
            }

            current = current with { New = Patched(current.New, sets) };
        }

        return patch.Count == 0 ? _none : patch;
    }

    public async Task<string?> AfterWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var stage = invocation.Operation == AlvoWriteOperation.Delete ? WriteHookStage.AfterDelete : WriteHookStage.AfterWrite;
        string? writer = null;
        foreach (var hook in Selected(invocation, stage).Where(hook => Fires(hook, invocation, stage)))
        {
            writer = (await RunAsync(hook, invocation, stage, cancellationToken).ConfigureAwait(false)).Wrote ? hook.Id : writer;
        }

        return writer;
    }

    private bool Registered(string entity, AlvoWriteOperation operation, bool inTransaction) =>
        registry.WriteHooks.Any(hook => hook.InTransaction == inTransaction && hook.Covers(entity) && hook.RunsOn(operation));

    /// <summary>
    /// The bound hooks of the stage, in order; a registered hook not bound fails the write closed (Ruling 7). Conditions are
    /// judged by the caller, per hook, against the row as the earlier hooks left it (Ruling 39).
    /// </summary>
    private List<BoundWriteHook> Selected(WriteInvocation invocation, WriteHookStage stage)
    {
        var registered = registry.WriteHooks.Where(hook => hook.Stage == stage && hook.Covers(invocation.Entity)).Select(hook => hook.Id).ToHashSet(StringComparer.Ordinal);
        if (registered.Count == 0)
        {
            return [];
        }

        var bindings = catalogs.Current is { } catalog ? binder.For(catalog) : ExtensionBindings.None;
        if (registered.FirstOrDefault(id => bindings.WriteHook(id) is null) is { } unbound)
        {
            throw Fail(unbound, HookFailedException.Unbound, cause: null);
        }

        return [.. bindings.WriteHooks.Where(hook => registered.Contains(hook.Id))];
    }

    private static bool Fires(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage) =>
        hook.ConditionFor(invocation.Entity) is not { } condition
        || CelInterpreter.EvaluatePredicate(condition, Current(invocation, stage), invocation.Old, invocation.Caller);

    private static AlvoRecord Current(WriteInvocation invocation, WriteHookStage stage) => stage switch
    {
        WriteHookStage.AfterWrite => invocation.Saved!,
        WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete => invocation.Old!,
        _ => invocation.New!,
    };

    private async Task<HookRun> RunAsync(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(hook.Registration.Settings.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var run = NewRun(hook, invocation, stage, linked.Token);
        using var activity = HookTelemetry.Start(hook.Id, invocation, stage);
        var started = time.GetTimestamp();
        try
        {
            using (HeldRows.Hold(invocation.Entity, HeldRows.RowOf(invocation)))
            {
                await hook.Registration.Invoker.InvokeAsync(invocation.Services!, run, linked.Token).ConfigureAwait(false);
            }
        }
        catch (Exception escaped)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Failed, time.GetElapsedTime(started), run.Operations);
            throw Escaped(run, escaped, deadline.IsCancellationRequested, cancellationToken);
        }

        return await ConcludedAsync(run, hook, activity, time.GetElapsedTime(started), deadline.IsCancellationRequested).ConfigureAwait(false);
    }

    /// <summary>What an exception escaping a hook becomes (Ruling 12): its run's own failure, depth or a nested hook's failure unchanged, a timeout, or <c>exception</c>.</summary>
    private Exception Escaped(HookRun run, Exception escaped, bool deadlinePassed, CancellationToken cancellationToken) =>
        run.Failure is { } failure ? failure
        : escaped is AlvoChainDepthExceededException or HookFailedException ? escaped
        : escaped is OperationCanceledException && cancellationToken.IsCancellationRequested ? escaped
        : deadlinePassed ? TimedOut(run)
        : Fail(run.Id, HookFailedException.Exception, escaped);

    private async Task<HookRun> ConcludedAsync(HookRun run, BoundWriteHook hook, Activity? activity, TimeSpan elapsed, bool deadlinePassed)
    {
        var rejection = run.Rejections.Winner();
        var failure = run.Failure
            ?? (deadlinePassed || elapsed > hook.Registration.Settings.Timeout ? TimedOut(run) : null)
            ?? (rejection is null ? InvalidSet(run) : null);
        if (failure is not null)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Failed, elapsed, run.Operations);
            throw failure;
        }

        if (rejection is not null)
        {
            HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Rejected, elapsed, run.Operations);
            await RecordAsync(run, rejection).ConfigureAwait(false);
            throw rejection;
        }

        HookTelemetry.Finish(activity, hook.Id, HookTelemetry.Succeeded, elapsed, run.Operations);
        return run;
    }

    private HookRun NewRun(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, CancellationToken token)
    {
        var data = stage == WriteHookStage.Validate ? services.GetRequiredService<IAlvoSessions>().For(OneHopDeeper(invocation.Caller)) : null;
        var run = new HookRun(hook.Id, invocation, Pointable(invocation.Entity), transaction: null, data, token) { Derived = Derived(invocation.Entity) };
        if (invocation.Transaction is { } inner)
        {
            run.Attach(new HookTransaction(inner, run, hook, logger));
        }

        return run;
    }

    private HookFailedException? InvalidSet(HookRun run)
    {
        if (run.Sets.Count == 0 || Entity(run.Invocation.Entity) is not { } entity)
        {
            return null;
        }

        if (HookSets.FirstProblem(run.Sets, entity) is not { } problem)
        {
            run.Replace(HookSets.Coerced(run.Sets, entity));
            return null;
        }

        HookLog.HookSetRefused(logger, run.Id, Hidden(entity.Name).Contains(problem.Field) ? "a hidden field" : problem.Field, problem.Problem);
        return Fail(run.Id, HookFailedException.InvalidSet, cause: null);
    }

    private HookFailedException TimedOut(HookRun run)
    {
        var failure = new HookFailedException(run.Id, HookFailedException.Timeout);
        AlvoFlow.Unit?.Blame(failure);
        HookLog.HookFailed(logger, run.Id, HookFailedException.Timeout, "-");

        return failure;
    }

    private HookFailedException Fail(string id, string reason, Exception? cause)
    {
        var failure = new HookFailedException(id, reason);
        AlvoFlow.Unit?.Poison(failure);
        HookLog.HookFailed(logger, id, reason, cause?.GetType().FullName ?? "-");

        return failure;
    }

    private async Task RecordAsync(HookRun run, AlvoWriteRejectedException rejection)
    {
        if (services.GetService<IHookRejectionLog>() is { } log)
        {
            await log.RecordAsync(
                new HookRejection(
                    time.GetUtcNow(), run.Id, run.Invocation.Entity, run.Invocation.Operation.ToString().ToLowerInvariant(),
                    rejection.Kind.ToString().ToLowerInvariant(), rejection.Code, AlvoIdempotency.IdentityOf(run.Invocation.Caller)),
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<HashSet<string>> DisabledAsync(CancellationToken cancellationToken) =>
        services.GetService<IDeliveryStore>() is { } store
            ? (await store.DisabledAsync(cancellationToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal)
            : [];

    private IReadOnlySet<string> Hidden(string entity) =>
        catalogs.Current is { } catalog && catalog.TryGetEntity(entity, out var policy)
            ? EntityPolicy.Flagged(policy.Hidden)
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The fields a rejection may point at: declared and never hidden (Ruling 53).</summary>
    private HashSet<string> Pointable(string entity)
    {
        var hidden = Hidden(entity);
        return Entity(entity)?.Fields.Select(field => field.Name).Where(name => !hidden.Contains(name)).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>The fields the store fills — <c>computed</c> and <c>rollup</c> — which an image of a row not yet stored lacks.</summary>
    private HashSet<string> Derived(string entity) =>
        Entity(entity)?.Fields.Where(field => field.ComputedExpression is not null || field.Rollup is not null).Select(field => field.Name).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

    private EntitySchema? Entity(string entity) =>
        catalogs.Current?.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

    private static AlvoRecord? Patched(AlvoRecord? record, IReadOnlyDictionary<string, object?> sets)
    {
        var patched = record;
        foreach (var (field, value) in sets)
        {
            patched = patched?.With(field, value);
        }

        return patched;
    }

    private static AlvoContext OneHopDeeper(AlvoContext context) =>
        context with { Provenance = context.Provenance with { Depth = AlvoEventProvenance.Effective(context).Depth + 1 } };
}
