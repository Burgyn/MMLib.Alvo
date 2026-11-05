using MMLib.Alvo.Data;
using MMLib.Alvo.Hooks;
using MMLib.Alvo.Hooks.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>The write hooks of the named entities (spec 2026-10-14 §4.4).</summary>
/// <remarks>
/// A write hook's <c>when</c> is a CEL condition over <c>new.</c> (the row the write would store; after-write: the stored row),
/// <c>old.</c> (absent on a create), <c>changed(field)</c>, <c>@user</c> and <c>@tenant</c>; a delete hook reads <c>old.</c> only,
/// and an every-entity (<c>"*"</c>) hook tests only <c>@user</c> and <c>@tenant</c>. Validate hooks run before a top-level
/// write's transaction and may call the network; the other four run inside it and may not — a hook type that takes a network
/// or data-port dependency refuses the start.
/// </remarks>
public sealed partial class AlvoEntityBuilder
{
    /// <summary>Runs a <typeparamref name="THook"/>, built per write, before a top-level create, update or replace's transaction.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens.</param>
    /// <param name="when">An optional CEL condition the write must meet.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Validate<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IValidateWriteHook =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromClass<THook, ValidateWrite<AlvoRecord>>(HookContexts<AlvoRecord>.Validate, (hook, write, ct) => hook.ValidateAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> before a top-level create, update or replace's transaction.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>ValidateWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder Validate(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.Validate, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.Validate), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeWriteHook =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromClass<THook, BeforeWrite<AlvoRecord>>(HookContexts<AlvoRecord>.Before, (hook, write, ct) => hook.BeforeWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, before the row is written.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeWrite, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.Before), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterWrite<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterWriteHook =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromClass<THook, AfterWrite<AlvoRecord>>(HookContexts<AlvoRecord>.After, (hook, write, ct) => hook.AfterWriteAsync(write, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a create, update or replace's transaction, after the row and its rollups.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterWrite&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored row.</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterWrite(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterWrite, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.After), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IBeforeDeleteHook =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromClass<THook, BeforeDelete<AlvoRecord>>(HookContexts<AlvoRecord>.BeforeDelete, (hook, delete, ct) => hook.BeforeDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, before the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking a <c>BeforeDelete&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder BeforeDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.BeforeDelete, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.BeforeDelete), when, configure);

    /// <summary>Runs a <typeparamref name="THook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <typeparam name="THook">The hook class.</typeparam>
    /// <param name="id">A stable id.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterDelete<THook>(string id, string? when = null, Action<AlvoHookOptions>? configure = null)
        where THook : class, IAfterDeleteHook =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromClass<THook, AfterDelete<AlvoRecord>>(HookContexts<AlvoRecord>.AfterDelete, (hook, delete, ct) => hook.AfterDeleteAsync(delete, ct)), when, configure);

    /// <summary>Runs <paramref name="hook"/> inside a delete's transaction, after the row is removed.</summary>
    /// <param name="id">A stable id.</param>
    /// <param name="hook">A delegate taking an <c>AfterDelete&lt;AlvoRecord&gt;</c>, and optionally a token and services.</param>
    /// <param name="when">An optional CEL condition over the removed row (<c>old.</c>).</param>
    /// <param name="configure">Optional hook options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder AfterDelete(string id, Delegate hook, string? when = null, Action<AlvoHookOptions>? configure = null) =>
        AddHook(id, WriteHookStage.AfterDelete, HookInvoker.FromDelegate(hook, HookContexts<AlvoRecord>.AfterDelete), when, configure);

    private AlvoEntityBuilder AddHook(string id, WriteHookStage stage, HookInvoker invoker, string? when, Action<AlvoHookOptions>? configure)
    {
        WriteHookRegistration.Add(_services, id, _entities, stage, when, configure, invoker);

        return this;
    }
}
