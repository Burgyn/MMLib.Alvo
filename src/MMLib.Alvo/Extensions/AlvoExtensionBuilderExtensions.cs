using MMLib.Alvo;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers host code with Alvo: <c>Entity</c> and <c>Entities</c> group what one or more entities run, and the
/// <c>Add*</c> verbs register the rest.
/// </summary>
public static class AlvoExtensionBuilderExtensions
{
    /// <summary>Registers host code for one entity, or for every entity with <c>"*"</c>.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="entity">The entity's name, or <c>"*"</c>: every entity, whose conditions may test only <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Registers the entity's handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder Entity(this IAlvoBuilder builder, string entity, Action<AlvoEntityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentNullException.ThrowIfNull(configure);
        configure(new AlvoEntityBuilder(builder.Services, [entity]));

        return builder;
    }

    /// <summary>Registers the same host code for several entities; each registration is one id bound to all of them.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="entities">The entities' names.</param>
    /// <param name="configure">Registers the entities' handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder Entities(this IAlvoBuilder builder, IEnumerable<string> entities, Action<AlvoEntityBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(entities);
        ArgumentNullException.ThrowIfNull(configure);
        IReadOnlyList<string> names = [.. entities];
        if (names.Count == 0 || names.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("Name at least one entity, and no blank one.", nameof(entities));
        }

        var repeated = names.GroupBy(name => name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (repeated is not null)
        {
            throw new ArgumentException($"'{repeated.Key}' is named twice; name each entity once.", nameof(entities));
        }

        configure(new AlvoEntityBuilder(builder.Services, names));

        return builder;
    }

    /// <summary>Registers host code for the entity <typeparamref name="T"/> maps, with conditions written as lambdas over it.</summary>
    /// <typeparam name="T">A class marked with <see cref="AlvoEntityAttribute"/>; its mapped properties are checked against the entity when the host starts.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="configure">Registers the entity's handlers.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> carries no <see cref="AlvoEntityAttribute"/>, or names <c>"*"</c>.</exception>
    public static IAlvoBuilder Entity<T>(this IAlvoBuilder builder, Action<AlvoEntityBuilder<T>> configure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        var entity = RecordShape.Of(typeof(T)).Entity
            ?? throw new ArgumentException($"Mark {typeof(T).Name} with [AlvoEntity(\"<entity>\")], so Alvo knows which entity it maps.", nameof(T));
        if (entity == EntityEvents.AllEntities)
        {
            throw new ArgumentException(
                $"{typeof(T).Name} maps [AlvoEntity(\"*\")], but a type maps one entity: register every-entity handlers with Entity(\"*\", …) and a CEL condition.",
                nameof(T));
        }

        configure(new AlvoEntityBuilder<T>(builder.Services, entity));

        return builder;
    }

    /// <summary>Runs <paramref name="handler"/> for every application or framework event named <paramref name="eventType"/>.</summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="eventType">The exact event name, such as <c>orders.approved</c>; an entity's events are handled through <c>Entity</c>.</param>
    /// <param name="handler">A delegate taking the payload as an <see cref="AlvoRecord"/>, and optionally the context, a token and services.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddEventHandler(
        this IAlvoBuilder builder,
        string id,
        string eventType,
        Delegate handler,
        string? when = null,
        Action<AlvoHandlerOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromDelegate(handler, typeof(AlvoRecord)));

        return builder;
    }

    /// <summary>Runs a <typeparamref name="THandler"/>, built per delivery, for every event named <paramref name="eventType"/>.</summary>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="eventType">The exact event name.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddEventHandler<THandler>(
        this IAlvoBuilder builder, string id, string eventType, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where THandler : class, IAlvoEventHandler<AlvoRecord>
    {
        ArgumentNullException.ThrowIfNull(builder);
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromClass<THandler, AlvoRecord>());

        return builder;
    }

    /// <summary>
    /// Runs a <typeparamref name="THandler"/>, built per delivery, for every application event <typeparamref name="TMessage"/>
    /// names, handing it the payload as a <typeparamref name="TMessage"/>.
    /// </summary>
    /// <typeparam name="TMessage">The payload type, marked with <see cref="AlvoEventAttribute"/>.</typeparam>
    /// <typeparam name="THandler">The handler class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its deliveries are found by it after a restart.</param>
    /// <param name="when">An optional CEL condition over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="configure">Optional handler options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TMessage"/> carries no <see cref="AlvoEventAttribute"/>.</exception>
    public static IAlvoBuilder AddEventHandler<TMessage, THandler>(
        this IAlvoBuilder builder, string id, string? when = null, Action<AlvoHandlerOptions>? configure = null)
        where TMessage : class
        where THandler : class, IAlvoEventHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(builder);
        var eventType = RecordShape.Of(typeof(TMessage)).EventType
            ?? throw new ArgumentException($"Mark {typeof(TMessage).Name} with [AlvoEvent(\"<event type>\")], so Alvo knows which event it is.", nameof(TMessage));
        HandledEventType.Ensure(eventType);
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(eventType), when, configure, HandlerInvoker.FromClass<THandler, TMessage>(), TypedHandler.ForPayload<TMessage>());

        return builder;
    }

    /// <summary>
    /// Declares an id this host no longer registers, and what happens at start-up to the work it left behind; without it
    /// a host whose store holds such work refuses to start.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">The retired id.</param>
    /// <param name="retirement">Dead-letter its unfinished deliveries, or discard them.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddRetiredExtension(this IAlvoBuilder builder, string id, AlvoRetirement retirement)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Enum.IsDefined(retirement))
        {
            throw new ArgumentOutOfRangeException(nameof(retirement), retirement, "Choose AlvoRetirement.DeadLetter or AlvoRetirement.Discard.");
        }

        ExtensionRegistrations.Add(builder.Services, sequence => new RetiredExtension(id, sequence, retirement));

        return builder;
    }
}
