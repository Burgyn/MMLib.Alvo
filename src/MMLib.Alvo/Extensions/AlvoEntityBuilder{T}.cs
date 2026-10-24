using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions.Internal;

using System.Linq.Expressions;

namespace MMLib.Alvo.Extensions;

/// <summary>
/// Registers host code for the entity <typeparamref name="T"/> maps, with messages over <typeparamref name="T"/> and
/// conditions written as lambdas over it — or as CEL strings; every registration is checked against the descriptor when
/// the host starts.
/// </summary>
/// <typeparam name="T">A class marked with <c>[AlvoEntity("…")]</c>.</typeparam>
/// <remarks>
/// A lambda <c>when</c> is translated into CEL as it is registered: an untranslatable one throws
/// <see cref="Data.AlvoTranslationException"/> from this call, naming the handler. The catalog shows the translated CEL.
/// Pass a lambda or a CEL string as <c>when</c>, or leave it out; an explicit <c>when: null</c> matches both overloads and
/// does not compile (CS0121).
/// </remarks>
public sealed class AlvoEntityBuilder<T>
    where T : class
{
    private readonly IServiceCollection _services;
    private readonly string _entity;

    internal AlvoEntityBuilder(IServiceCollection services, string entity)
    {
        _services = services;
        _entity = entity;
    }

    /// <summary>Runs <paramref name="handler"/> after a record is created, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id, lower-case letters, digits and hyphens: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated(string id, Delegate handler, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<T>)), Translated(id, HandlerVerb.Created, when), configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoCreated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over <c>new.</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromDelegate(handler, typeof(AlvoCreated<T>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated<THandler>(string id, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<T>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<T>>(), Translated(id, HandlerVerb.Created, when), configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over <c>new.</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnCreated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoCreated<T>> =>
        Add(id, HandlerVerb.Created, HandlerInvoker.FromClass<THandler, AlvoCreated<T>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is updated or replaced, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoUpdated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">A condition over <c>e.New</c>, <c>e.Old</c>, <c>e.Changed(…)</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnUpdated(string id, Delegate handler, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromDelegate(handler, typeof(AlvoUpdated<T>)), Translated(id, HandlerVerb.Updated, when), configure);

    /// <summary>Runs <paramref name="handler"/> after a record is updated or replaced.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoUpdated&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnUpdated(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromDelegate(handler, typeof(AlvoUpdated<T>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is updated or replaced, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">A condition over <c>e.New</c>, <c>e.Old</c>, <c>e.Changed(…)</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnUpdated<THandler>(string id, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoUpdated<T>> =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromClass<THandler, AlvoUpdated<T>>(), Translated(id, HandlerVerb.Updated, when), configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is updated or replaced.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over both images.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnUpdated<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoUpdated<T>> =>
        Add(id, HandlerVerb.Updated, HandlerInvoker.FromClass<THandler, AlvoUpdated<T>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is deleted, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoDeleted&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">A condition over <c>e.Old</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnDeleted(string id, Delegate handler, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromDelegate(handler, typeof(AlvoDeleted<T>)), Translated(id, HandlerVerb.Deleted, when), configure);

    /// <summary>Runs <paramref name="handler"/> after a record is deleted.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoDeleted&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnDeleted(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromDelegate(handler, typeof(AlvoDeleted<T>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is deleted, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">A condition over <c>e.Old</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnDeleted<THandler>(string id, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoDeleted<T>> =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromClass<THandler, AlvoDeleted<T>>(), Translated(id, HandlerVerb.Deleted, when), configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is deleted.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the deleted record (<c>old.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnDeleted<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoDeleted<T>> =>
        Add(id, HandlerVerb.Deleted, HandlerInvoker.FromClass<THandler, AlvoDeleted<T>>(), when, configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created or updated, when <paramref name="when"/> holds.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoSaved&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnSaved(string id, Delegate handler, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromDelegate(handler, typeof(AlvoSaved<T>)), Translated(id, HandlerVerb.Saved, when), configure);

    /// <summary>Runs <paramref name="handler"/> after a record is created or updated.</summary>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="handler">A delegate taking an <c>AlvoSaved&lt;T&gt;</c>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnSaved(string id, Delegate handler, string? when = null, Action<AlvoHandlerOptions>? configure = null) =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromDelegate(handler, typeof(AlvoSaved<T>)), when, configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created or updated, when <paramref name="when"/> holds.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">A condition over <c>e.New</c> and <c>e.Caller</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnSaved<THandler>(string id, Expression<Func<EventCondition<T>, bool>> when, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoSaved<T>> =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromClass<THandler, AlvoSaved<T>>(), Translated(id, HandlerVerb.Saved, when), configure);

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, after a record is created or updated.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over the stored record (<c>new.</c>).</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns>This builder.</returns>
    public AlvoEntityBuilder<T> OnSaved<THandler>(string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoSaved<T>> =>
        Add(id, HandlerVerb.Saved, HandlerInvoker.FromClass<THandler, AlvoSaved<T>>(), when, configure);

    private static string Translated(string id, HandlerVerb verb, Expression<Func<EventCondition<T>, bool>> when)
    {
        ArgumentNullException.ThrowIfNull(when);

        return ConditionTranslator.Translate(when, id, verb);
    }

    private AlvoEntityBuilder<T> Add(string id, HandlerVerb verb, HandlerInvoker invoker, string? when, Action<AlvoHandlerOptions>? configure)
    {
        ExtensionRegistrations.AddHandler(_services, id, new EntityEvents([_entity], verb), when, configure, invoker, TypedHandler.ForEntity<T>(verb));

        return this;
    }
}
