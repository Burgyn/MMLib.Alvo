using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Expressions;
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
/// <b>Nothing it holds reaches the network:</b> its own dependencies are the catalog, the binder, the clock, the host-function
/// budget, a logger and the root provider it resolves the session factory, the delivery store and the rejection log from — the
/// architecture fact over <c>WriteExtensionRunner</c>'s constructor (§4.4.5) pins that list.
/// </remarks>
internal sealed class WriteExtensionRunner(
    ExtensionRegistry registry, ExtensionBinder binder, IPolicyCatalogProvider catalogs, IServiceProvider services, TimeProvider time,
    CelFunctionBudgetPolicy budget, ILogger<WriteExtensionRunner> logger) : IWriteExtensionRunner
{
    private static readonly IReadOnlyDictionary<string, object?> _none = new Dictionary<string, object?>(StringComparer.Ordinal);

    public bool Validates(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: false);

    public bool RunsInTransaction(string entity, AlvoWriteOperation operation) => Registered(entity, operation, inTransaction: true);

    public async Task ValidateAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var catalog = catalogs.Current;
        var hooks = Selected(invocation, WriteHookStage.Validate, catalog);
        if (hooks.Count == 0)
        {
            return;
        }

        var disabled = await DisabledAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = services.GetRequiredService<IServiceScopeFactory>().CreateAsyncScope();
        var scoped = invocation with { Services = scope.ServiceProvider };
        foreach (var hook in hooks.Where(hook => !disabled.Contains(hook.Id) && Fires(hook, scoped, WriteHookStage.Validate, (catalog, _none), cancellationToken)))
        {
            await RunAsync(hook, scoped, WriteHookStage.Validate, catalog, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyDictionary<string, object?>> BeforeWriteAsync(WriteInvocation invocation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var stage = invocation.Operation == AlvoWriteOperation.Delete ? WriteHookStage.BeforeDelete : WriteHookStage.BeforeWrite;
        var patch = new Dictionary<string, object?>(StringComparer.Ordinal);
        var current = invocation;
        var catalog = catalogs.Current;
        foreach (var hook in Selected(invocation, stage, catalog))
        {
            if (!Fires(hook, current, stage, (catalog, patch), cancellationToken))
            {
                continue;
            }

            var sets = (await RunAsync(hook, current, stage, catalog, cancellationToken).ConfigureAwait(false)).Sets;
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
        var catalog = catalogs.Current;
        foreach (var hook in Selected(invocation, stage, catalog).Where(hook => Fires(hook, invocation, stage, (catalog, _none), cancellationToken)))
        {
            writer = (await RunAsync(hook, invocation, stage, catalog, cancellationToken).ConfigureAwait(false)).Wrote ? hook.Id : writer;
        }

        return writer;
    }

    private bool Registered(string entity, AlvoWriteOperation operation, bool inTransaction) =>
        registry.WriteHooks.Any(hook => hook.InTransaction == inTransaction && hook.Covers(entity) && hook.RunsOn(operation));

    /// <summary>
    /// The bound hooks of the stage, in order; a registered hook not bound fails the write closed (Ruling 7). Conditions are
    /// judged by the caller, per hook, against the row as the earlier hooks left it (Ruling 39). <paramref name="catalog"/> is the
    /// one the stage read when it began: the binding, the pointers and the <c>Set</c>s of a stage are judged against one
    /// catalog, whatever an apply does meanwhile (Ruling 63).
    /// </summary>
    private List<BoundWriteHook> Selected(WriteInvocation invocation, WriteHookStage stage, PolicyCatalog? catalog)
    {
        var registered = registry.WriteHooks.Where(hook => hook.Stage == stage && hook.Covers(invocation.Entity)).Select(hook => hook.Id).ToHashSet(StringComparer.Ordinal);
        if (registered.Count == 0)
        {
            return [];
        }

        var bindings = catalog is null ? ExtensionBindings.None : binder.For(catalog);
        if (registered.FirstOrDefault(id => bindings.WriteHook(id) is null) is { } unbound)
        {
            throw FailBeforeRun(unbound, HookFailedException.Unbound, cause: null);
        }

        return [.. bindings.WriteHooks.Where(hook => registered.Contains(hook.Id))];
    }

    /// <summary>
    /// Whether <paramref name="hook"/>'s <c>when</c> holds. A host function failing in it is the hook's failure, never a skip;
    /// a built-in failing on the caller's data escapes unchanged, answered as any condition's built-in failure (Ruling 63). The
    /// <c>when</c> is one host-function budget pass, linked with the write's token; past the budget it is the hook's failure.
    /// The write's own cancellation reaching a token-aware function is the write's cancellation, as in a hook's body. A
    /// built-in's refusal is the caller's only when the <c>when</c> reads no value a hook wrote (E1, Ruling 26).
    /// </summary>
    /// <param name="hook">The hook whose <c>when</c> is judged.</param>
    /// <param name="invocation">The write, as the earlier hooks of the stage left it.</param>
    /// <param name="stage">The stage.</param>
    /// <param name="written">The stage's catalog and the host <c>Set</c>s so far — what a refusal is attributed against.</param>
    /// <param name="cancellationToken">The write's token.</param>
    private bool Fires(
        BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage,
        (PolicyCatalog? Catalog, IReadOnlyDictionary<string, object?> Sets) written, CancellationToken cancellationToken)
    {
        if (hook.ConditionFor(invocation.Entity) is not { } condition)
        {
            return true;
        }

        try
        {
            using var pass = budget.Enter(cancellationToken);
            return CelInterpreter.EvaluatePredicate(condition, Current(invocation, stage), invocation.Old, invocation.Caller);
        }
        catch (Exception thrown) when (WriteCancelled(thrown, cancellationToken))
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (CelFunctionException refused) when (refused.OnCallerData && !OnCallersData(condition, invocation, stage, written))
        {
            throw refused.NotTheCallers();
        }
        catch (Exception thrown) when (thrown is not CelFunctionException { IsHost: false })
        {
            throw FailBeforeRun(hook.Id, HookFailedException.Exception, thrown);
        }
    }

    /// <summary>
    /// Whether <paramref name="thrown"/> is the write's cancellation as a host function received it — not an overrun, which
    /// carries Alvo's reason. Rethrown bare: a wrapped function failure would be answered <c>function-failed</c> and logged.
    /// </summary>
    /// <remarks>
    /// Only the wrapped shape is reachable: <c>CelFunction.Run</c> wraps whatever a host body throws, and anything else that
    /// escapes the interpreter on this fail-closed path is its defect, never a bare <see cref="OperationCanceledException"/>.
    /// </remarks>
    private static bool WriteCancelled(Exception thrown, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        && thrown is CelFunctionException { IsHost: true, Reason: null, InnerException: OperationCanceledException };

    /// <summary>
    /// Whether a refusal in <paramref name="condition"/> stays the caller's, judged on the failure path only: never after the
    /// write (the saved row carries defaults, rollups and hook writes), always in validate (no hook has written yet), and
    /// before it only when the <c>when</c> reads no field a declarative before-hook of this operation mutates or an earlier
    /// host hook set. Conservative: the whole expression is judged.
    /// </summary>
    private static bool OnCallersData(
        CompiledExpression condition, WriteInvocation invocation, WriteHookStage stage,
        (PolicyCatalog? Catalog, IReadOnlyDictionary<string, object?> Sets) written) => stage switch
        {
            WriteHookStage.Validate => true,
            WriteHookStage.AfterWrite or WriteHookStage.AfterDelete => false,
            _ => !CelDataOrigin.Reads(condition.Root, HookWritten(written.Catalog, invocation, written.Sets)),
        };

    /// <summary>The fields hooks may have written before this <c>when</c>: the declarative before-hooks' mutate targets and the host <c>Set</c>s so far.</summary>
    private static HashSet<string> HookWritten(PolicyCatalog? catalog, WriteInvocation invocation, IReadOnlyDictionary<string, object?> sets)
    {
        var fields = sets.Keys.ToHashSet(StringComparer.Ordinal);
        if (catalog is not null && catalog.TryGetEntity(invocation.Entity, out var policy))
        {
            fields.UnionWith(policy.BeforeHooks.For(DeclarativeOperation(invocation)).SelectMany(hook => hook.Mutations).Select(mutation => mutation.Field));
        }

        return fields;
    }

    /// <summary>The declarative before-hook list a write runs: a replace that creates runs the create list.</summary>
    private static DataOperation DeclarativeOperation(WriteInvocation invocation) => invocation.Operation switch
    {
        AlvoWriteOperation.Create => DataOperation.Create,
        AlvoWriteOperation.Delete => DataOperation.Delete,
        _ => invocation.Old is null ? DataOperation.Create : DataOperation.Update,
    };

    private static AlvoRecord Current(WriteInvocation invocation, WriteHookStage stage) => stage switch
    {
        WriteHookStage.AfterWrite => invocation.Saved!,
        WriteHookStage.BeforeDelete or WriteHookStage.AfterDelete => invocation.Old!,
        _ => invocation.New!,
    };

    private async Task<HookRun> RunAsync(
        BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, PolicyCatalog? catalog, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(hook.Registration.Settings.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var run = NewRun(hook, invocation, stage, catalog, linked.Token);
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

        return await ConcludedAsync(run, hook, catalog, activity, time.GetElapsedTime(started), deadline.IsCancellationRequested).ConfigureAwait(false);
    }

    /// <summary>What an exception escaping a hook becomes (Ruling 12): its run's own failure, depth or a nested hook's failure unchanged, a timeout, or <c>exception</c>.</summary>
    private Exception Escaped(HookRun run, Exception escaped, bool deadlinePassed, CancellationToken cancellationToken) =>
        run.Failure is { } failure ? failure
        : escaped is AlvoChainDepthExceededException or HookFailedException ? escaped
        : escaped is OperationCanceledException && cancellationToken.IsCancellationRequested ? escaped
        : deadlinePassed ? TimedOut(run)
        : Fail(run.Id, HookFailedException.Exception, escaped);

    private async Task<HookRun> ConcludedAsync(
        HookRun run, BoundWriteHook hook, PolicyCatalog? catalog, Activity? activity, TimeSpan elapsed, bool deadlinePassed)
    {
        var rejection = run.Rejections.Winner();
        var failure = run.Failure
            ?? (deadlinePassed || elapsed > hook.Registration.Settings.Timeout ? TimedOut(run) : null)
            ?? (rejection is null ? InvalidSet(run, catalog) : null);
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

    private HookRun NewRun(BoundWriteHook hook, WriteInvocation invocation, WriteHookStage stage, PolicyCatalog? catalog, CancellationToken token)
    {
        var data = stage == WriteHookStage.Validate ? services.GetRequiredService<IAlvoSessions>().For(OneHopDeeper(invocation.Caller)) : null;
        var entity = Entity(catalog, invocation.Entity);
        var run = new HookRun(hook.Id, invocation, Pointable(catalog, entity), transaction: null, data, token) { Derived = Derived(entity) };
        if (invocation.Transaction is { } inner)
        {
            run.Attach(new HookTransaction(inner, run, hook, logger));
        }

        return run;
    }

    /// <summary>The <c>Set</c>s' failure, if any; with no schema for the entity, every <c>Set</c> is undeclared (Ruling 63).</summary>
    private HookFailedException? InvalidSet(HookRun run, PolicyCatalog? catalog)
    {
        var sets = run.Sets;
        if (sets.Count == 0)
        {
            return null;
        }

        if (Entity(catalog, run.Invocation.Entity) is not { } entity)
        {
            return SetRefused(run, sets.Keys.First(), HookSets.Undeclared);
        }

        if (HookSets.FirstProblem(sets, entity) is not { } problem)
        {
            run.Replace(HookSets.Coerced(sets, entity));
            return null;
        }

        return SetRefused(run, Hidden(catalog, entity.Name).Contains(problem.Field) ? "a hidden field" : problem.Field, problem.Problem);
    }

    private HookFailedException SetRefused(HookRun run, string field, string problem)
    {
        HookLog.HookSetRefused(logger, run.Id, field, problem);
        return Fail(run.Id, HookFailedException.InvalidSet, cause: null);
    }

    private HookFailedException TimedOut(HookRun run)
    {
        var failure = new HookFailedException(run.Id, HookFailedException.Timeout);
        AlvoFlow.Unit?.Blame(failure);
        HookLog.HookFailed(logger, run.Id, HookFailedException.Timeout, "-");

        return failure;
    }

    /// <summary>A hook that fails before its run starts is still a failed run for the meters and the stats (Ruling 28).</summary>
    private HookFailedException FailBeforeRun(string id, string reason, Exception? cause)
    {
        HookTelemetry.Finish(activity: null, id, HookTelemetry.Failed, TimeSpan.Zero, operations: 0);

        return Fail(id, reason, cause);
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

    private static IReadOnlySet<string> Hidden(PolicyCatalog? catalog, string entity) =>
        catalog is not null && catalog.TryGetEntity(entity, out var policy)
            ? policy.FlaggedHidden
            : new HashSet<string>(StringComparer.Ordinal);

    /// <summary>The fields a rejection may point at: declared and never hidden (Ruling 53).</summary>
    private static HashSet<string> Pointable(PolicyCatalog? catalog, EntitySchema? entity)
    {
        if (entity is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var hidden = Hidden(catalog, entity.Name);
        return entity.Fields.Select(field => field.Name).Where(name => !hidden.Contains(name)).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The fields the store fills — <c>computed</c> and <c>rollup</c> — which an image of a row not yet stored lacks.</summary>
    private static HashSet<string> Derived(EntitySchema? entity) =>
        entity?.Fields.Where(field => field.ComputedExpression is not null || field.Rollup is not null || field.IsVirtual).Select(field => field.Name).ToHashSet(StringComparer.Ordinal)
            ?? new HashSet<string>(StringComparer.Ordinal);

    private static EntitySchema? Entity(PolicyCatalog? catalog, string entity) =>
        catalog?.Schema.Entities.FirstOrDefault(candidate => string.Equals(candidate.Name, entity, StringComparison.Ordinal));

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
