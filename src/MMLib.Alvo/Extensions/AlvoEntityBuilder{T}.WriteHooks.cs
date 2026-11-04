using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions;

/// <summary>The write hooks of the entity <typeparamref name="T"/> maps (spec 2026-10-14 §4.4), with contexts over <typeparamref name="T"/>.</summary>
/// <remarks>
/// A lambda <c>when</c> is a <see cref="WriteCondition{T}"/> translated into CEL as the hook is registered — an untranslatable
/// one throws <see cref="Data.AlvoTranslationException"/> from this call, naming the hook; a string <c>when</c> is CEL over
/// <c>new.</c>, <c>old.</c>, <c>changed(field)</c>, <c>@user</c> and <c>@tenant</c> (a delete hook reads <c>old.</c> only).
/// Validate hooks run before a top-level write's transaction and may call the network; the other four run inside it and may
/// not — a hook type that takes a network or data-port dependency refuses the start.
/// </remarks>
public sealed partial class AlvoEntityBuilder<T>
{
    /// <summary>Runs a <typeparamref name="THook"/>, built per write, before a top-level create, update or replace's transaction, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Validate<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IValidateWriteHook<T> =>
        AddHook(id, WriteHookStage.Validate, ClassHook<THook, ValidateWrite<T>>(HookContexts<T>.Validate, (hook, write, ct) => hook.ValidateAsync(write, ct)), Translated(id, when), configure);

    /// <summary>Runs a <typeparamref name="THook"/>, built per write, before a top-level create, update or replace's transaction.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition the write must meet.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Validate<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IValidateWriteHook<T> =>
        AddHook(id, WriteHookStage.Validate, ClassHook<THook, ValidateWrite<T>>(HookContexts<T>.Validate, (hook, write, ct) => hook.ValidateAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> before a top-level create, update or replace's transaction, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>ValidateWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Validate(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromDelegate(hook, HookContexts<T>.Validate), Translated(id, when), configure);

    /// <summary>Runs <paramref name="hook"/> before a top-level create, update or replace's transaction.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>ValidateWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition the write must meet.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> Validate(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromDelegate(hook, HookContexts<T>.Validate), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, before the row is written, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeWrite<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook<T> =>
        AddHook(id, WriteHookStage.BeforeWrite, ClassHook<THook, BeforeWrite<T>>(HookContexts<T>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), Translated(id, when), configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook<T> =>
        AddHook(id, WriteHookStage.BeforeWrite, ClassHook<THook, BeforeWrite<T>>(HookContexts<T>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, before the row is written, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeWrite(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.Before), Translated(id, when), configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.Before), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, after the row and its rollups, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterWrite<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterWriteHook<T> =>
        AddHook(id, WriteHookStage.AfterWrite, ClassHook<THook, AfterWrite<T>>(HookContexts<T>.After, (hook, write, ct) => hook.AfterWriteAsync(write, ct)), Translated(id, when), configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterWriteHook<T> =>
        AddHook(id, WriteHookStage.AfterWrite, ClassHook<THook, AfterWrite<T>>(HookContexts<T>.After, (hook, write, ct) => hook.AfterWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, after the row and its rollups, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterWrite(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.After), Translated(id, when), configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterWrite&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromDelegate(hook, HookContexts<T>.After), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, before the row is removed, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeDelete<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeDeleteHook<T> =>
        AddHook(id, WriteHookStage.BeforeDelete, ClassHook<THook, BeforeDelete<T>>(HookContexts<T>.BeforeDelete, (hook, delete, ct) => hook.BeforeDeleteAsync(delete, ct)), Translated(id, when), configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeDeleteHook<T> =>
        AddHook(id, WriteHookStage.BeforeDelete, ClassHook<THook, BeforeDelete<T>>(HookContexts<T>.BeforeDelete, (hook, delete, ct) => hook.BeforeDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, before the row is removed, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeDelete&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeDelete(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromDelegate(hook, HookContexts<T>.BeforeDelete), Translated(id, when), configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeDelete&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> BeforeDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromDelegate(hook, HookContexts<T>.BeforeDelete), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, after the row is removed, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterDelete<THook>(string id, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterDeleteHook<T> =>
        AddHook(id, WriteHookStage.AfterDelete, ClassHook<THook, AfterDelete<T>>(HookContexts<T>.AfterDelete, (hook, delete, ct) => hook.AfterDeleteAsync(delete, ct)), Translated(id, when), configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterDeleteHook<T> =>
        AddHook(id, WriteHookStage.AfterDelete, ClassHook<THook, AfterDelete<T>>(HookContexts<T>.AfterDelete, (hook, delete, ct) => hook.AfterDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, after the row is removed, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterDelete&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">A condition over <c>w.New</c>, <c>w.Old</c>, <c>w.Changed(…)</c> and <c>w.Caller</c>.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterDelete(string id, Delegate hook, Expression<Func<WriteCondition<T>, bool>> when, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromDelegate(hook, HookContexts<T>.AfterDelete), Translated(id, when), configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterDelete&lt;T&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> AfterDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromDelegate(hook, HookContexts<T>.AfterDelete), when, configure);

    private static HookInvoker ClassHook<THook, TContext>(Func<HookRun, TContext> context, Func<THook, TContext, CancellationToken, ValueTask> call)
        where THook : class => HookInvoker.FromClass(context, call);

    private static string Translated(string id, Expression<Func<WriteCondition<T>, bool>> when)
    {
        ArgumentNullException.ThrowIfNull(when);
        return WriteConditionTranslator.Translate(when, id);
    }

    private AlvoEntityBuilder<T> AddHook(string id, WriteHookStage stage, HookInvoker invoker, string? when, Action<AlvoHookOptions>? configure)
    {
        WriteHookRegistration.Add(_services, id, [_entity], stage, when, configure, invoker, RecordShape.Of(typeof(T)));

        return this;
    }
}
