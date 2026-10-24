using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// Registers host code for the entities named in <c>Entity(…)</c> or <c>Entities(…)</c>; every registration is checked
/// against the descriptor when the host starts, and all problems are reported at once.
/// </summary>
/// <remarks>
/// A handler's <c>when</c> is a CEL condition over <c>new.</c> and <c>old.</c> (as the verb's event carries them),
/// <c>changed(field)</c> on updates, <c>@user</c> and <c>@tenant</c>. It is judged after the write commits, before any
/// delivery exists: an event it does not match costs no delivery.
/// </remarks>
public sealed class AlvoEntityBuilder
{
    private readonly IServiceCollection _services;
    private readonly IReadOnlyList<string> _entities;

    internal AlvoEntityBuilder(IServiceCollection services, IReadOnlyList<string> entities)
    {
        _services = services;
        _entities = entities;
    }

    /// <summary>Runs <paramref name="handler"/> after a record is created.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;AlvoRecord&gt;</c>, and optionally the <see cref="AlvoEventContext"/>, a <see cref="CancellationToken"/> and services.</param>
    /// <param name="when">An optional CEL condition the created record must meet.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnCreated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition the created record must meet.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnCreated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<AlvoRecord>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is updated or replaced.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoUpdated&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnUpdated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromDelegate(handler, typeof(AlvoUpdated<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is updated or replaced.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnUpdated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoUpdated<AlvoRecord>> =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromClass<THandler, AlvoUpdated<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is deleted.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoDeleted&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnDeleted(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromDelegate(handler, typeof(AlvoDeleted<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is deleted.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnDeleted<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoDeleted<AlvoRecord>> =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromClass<THandler, AlvoDeleted<AlvoRecord>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created or updated.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoSaved&lt;AlvoRecord&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnSaved(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromDelegate(handler, typeof(AlvoSaved<AlvoRecord>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/> after a record is created or updated.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder OnSaved<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoSaved<AlvoRecord>> =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromClass<THandler, AlvoSaved<AlvoRecord>>(), when, configure);

    private AlvoEntityBuilder Add(
        string id, HandlerVerb verb, HandlerInvoker invoker, string? when, Action<AlvoHandlerOptions>? configure)
    {
        ExtensionRegistrations.AddHandler(_services, id, new EntityEvents(_entities, verb), when, configure, invoker);

        return this;
    }
}
