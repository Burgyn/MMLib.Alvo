using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo;
using MMLib.Alvo.Commands;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using MMLib.Alvo.Events.Internal;
using MMLib.Alvo.Extensions;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Functions;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Scheduling;
using MMLib.Alvo.Secrets;
using MMLib.Alvo.Secrets.Internal;
using System.Linq.Expressions;

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
    /// Runs a <typeparamref name="TJob"/>, built per run, on <paramref name="cron"/> in <paramref name="timeZone"/>: one run
    /// per occurrence across every replica — once per tenant with <see cref="AlvoScheduleScope.EachTenant"/> — as a delivery
    /// that is retried, recorded and runnable on demand. An occurrence missed while no replica ran runs once, not once per miss.
    /// </summary>
    /// <typeparam name="TJob">The job class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: its next run and its deliveries are found by it after a restart.</param>
    /// <param name="cron">Five fields — minute, hour, day of month, month, day of week — such as <c>0 2 * * *</c>; checked when the host starts.</param>
    /// <param name="timeZone">The IANA zone the cron is read in; default <c>UTC</c>. Daylight-saving changes follow the zone.</param>
    /// <param name="configure">Optional schedule options.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    public static IAlvoBuilder AddSchedule<TJob>(
        this IAlvoBuilder builder, string id, string cron, string timeZone = "UTC", Action<AlvoScheduleOptions>? configure = null)
        where TJob : class, IAlvoScheduledJob
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(cron);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        var options = new AlvoScheduleOptions();
        configure?.Invoke(options);
        builder.Services.TryAddTransient<TJob>();
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(ScheduleEvents.TypeFor(id)), when: null, handler => JobSettings(handler, options),
            HandlerInvoker.ForJob<TJob>(), typed: null, new JobSpec(DeliveryKind.Schedule, cron, timeZone, options.Scope, AfterRevision: null));

        return builder;
    }

    /// <summary>
    /// Runs a <typeparamref name="TMigration"/>, built per run, once: when the applied descriptor first reaches
    /// <paramref name="afterRevision"/> or later — once per tenant the <see cref="IAlvoTenantCatalog"/> lists when the schema has
    /// a scoped entity — as a delivery that is retried and recorded. Neither a restart, a rollback nor a re-apply runs it again.
    /// </summary>
    /// <typeparam name="TMigration">The migration class.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="id">A stable id: whether it has run is found by it after a restart.</param>
    /// <param name="afterRevision">The applied descriptor revision it waits for; at least 1.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="afterRevision"/> is below 1.</exception>
    public static IAlvoBuilder AddDataMigration<TMigration>(this IAlvoBuilder builder, string id, int afterRevision)
        where TMigration : class, IAlvoDataMigration
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(afterRevision, 1);
        var options = new AlvoScheduleOptions();
        builder.Services.TryAddTransient<TMigration>();
        ExtensionRegistrations.AddHandler(
            builder.Services, id, new NamedEvent(ScheduleEvents.MigrationTypeFor(id)), when: null, handler => JobSettings(handler, options),
            HandlerInvoker.ForJob<TMigration>(), typed: null, new JobSpec(DeliveryKind.Migration, null, null, AlvoScheduleScope.Global, afterRevision));

        return builder;
    }

    /// <summary>
    /// Registers a host function: <typeparamref name="TFunction"/>, built per run, called by a descriptor's <c>function</c>
    /// action named <paramref name="name"/> after the triggering write commits, as a delivery that is retried and recorded. The
    /// action's <c>input</c> is checked against the input type's JSON Schema when the descriptor is applied.
    /// </summary>
    /// <typeparam name="TFunction">A concrete class implementing <c>IAlvoFunction&lt;TInput&gt;</c> exactly once.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">
    /// The name a <c>function</c> action calls it by, and its id: a lower-case letter, then up to 62 lower-case letters, digits,
    /// hyphens and underscores — <c>issue-invoice</c>.
    /// </param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <typeparamref name="TFunction"/> is no function, or <paramref name="name"/> is outside the name grammar.
    /// </exception>
    public static IAlvoBuilder AddFunction<TFunction>(this IAlvoBuilder builder, string name)
        where TFunction : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        if (!ExtensionIds.IsFunctionName(name))
        {
            throw new ArgumentException(
                $"'{name}' is not a function name: use a lower-case letter, then up to 62 lower-case letters, digits, hyphens and underscores — 'issue-invoice'.",
                nameof(name));
        }

        var input = FunctionInputOf<TFunction>();
        var schema = FunctionInputSchema.For(input);
        builder.Services.AddKeyedTransient<TFunction>(name);
        ExtensionRegistrations.Add(builder.Services, sequence => new FunctionRegistration(name, sequence, typeof(TFunction), input, schema));

        return builder;
    }

    /// <summary>
    /// Declares a secret this host cannot run without: the start is refused while the secret store does not hold a non-empty
    /// value under <paramref name="name"/>. Only the name is ever logged or reported — never the value.
    /// </summary>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">The secret's name in the store: lower case, starting with a letter, at most 64 letters, digits, dots, dashes and underscores.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is not a secret name.</exception>
    public static IAlvoBuilder AddRequiredSecret(this IAlvoBuilder builder, string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(name);
        builder.Services.AddSingleton(new RequiredSecret(SecretName.Parse(name)));

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

    /// <summary>
    /// Registers an internal command: <typeparamref name="TCommand"/>, invoked through <c>IAlvoCommands.InvokeAsync</c>
    /// under <paramref name="access"/>.
    /// </summary>
    /// <typeparam name="TCommand">A concrete class implementing <c>IAlvoCommand&lt;TInput&gt;</c> exactly once.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">
    /// The command's name: lower-case segments of letters, digits and underscores, each starting with a letter, separated by
    /// dots — <c>confirm_booking</c> or <c>billing.issue_invoice</c>.
    /// </param>
    /// <param name="access">The access rule: CEL over <c>@user</c> and <c>@tenant</c>; <c>'true'</c> admits every caller.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TCommand"/> is no command, or <paramref name="access"/> is empty.</exception>
    public static IAlvoBuilder AddCommand<TCommand>(this IAlvoBuilder builder, string name, string access)
        where TCommand : class =>
        Commanded<TCommand>(builder, name, CommandAccess.Of(access));

    /// <summary>Registers an internal command whose access rule is a lambda over the caller, translated to CEL here.</summary>
    /// <typeparam name="TCommand">A concrete class implementing <c>IAlvoCommand&lt;TInput&gt;</c> exactly once.</typeparam>
    /// <param name="builder">The Alvo builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="access">The access rule, e.g. <c>caller =&gt; caller.HasRole("dispatcher")</c>.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="TCommand"/> is no command.</exception>
    /// <exception cref="AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static IAlvoBuilder AddCommand<TCommand>(this IAlvoBuilder builder, string name, Expression<Func<AlvoConditionCaller, bool>> access)
        where TCommand : class =>
        Commanded<TCommand>(builder, name, CommandAccess.Typed(access, name));

    private static void JobSettings(AlvoHandlerOptions handler, AlvoScheduleOptions options)
    {
        handler.Timeout = options.Timeout;
        handler.Retry = options.Retry;
        handler.Description = options.Description;
        handler.RunAs = AlvoRunAs.System;
        handler.Ordering = AlvoOrdering.None;
    }

    private static IAlvoBuilder Commanded<TCommand>(IAlvoBuilder builder, string name, CommandAccess access)
        where TCommand : class
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var input = InputOf<TCommand>();
        builder.Services.TryAddTransient<TCommand>();
        ExtensionRegistrations.Add(builder.Services, sequence => new CommandRegistration(name, sequence, access, null, null, typeof(TCommand), input));

        return builder;
    }

    private static Type FunctionInputOf<TFunction>()
        where TFunction : class
    {
        var function = typeof(TFunction);
        if (function.IsAbstract)
        {
            throw new ArgumentException(
                $"{function.Name} is abstract, so it cannot be created to run: register a concrete class that implements IAlvoFunction<TInput>.",
                nameof(TFunction));
        }

        var implemented = function.GetInterfaces()
            .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAlvoFunction<>))
            .ToList();

        return implemented is [var only]
            ? only.GetGenericArguments()[0]
            : throw new ArgumentException(
                $"{function.Name} must implement IAlvoFunction<TInput> exactly once to be a function; it implements it {implemented.Count} times.",
                nameof(TFunction));
    }

    private static Type InputOf<TCommand>()
        where TCommand : class
    {
        var command = typeof(TCommand);
        if (command.IsAbstract)
        {
            throw new ArgumentException(
                $"{command.Name} is abstract, so it cannot be created to run: register a concrete class that implements IAlvoCommand<TInput>.",
                nameof(TCommand));
        }

        var implemented = command.GetInterfaces()
            .Where(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IAlvoCommand<>))
            .ToList();

        return implemented is [var only]
            ? only.GetGenericArguments()[0]
            : throw new ArgumentException(
                $"{command.Name} must implement IAlvoCommand<TInput> exactly once to be a command; it implements it {implemented.Count} times.",
                nameof(TCommand));
    }
}
