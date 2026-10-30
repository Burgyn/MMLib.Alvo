using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Commands.Internal;
using MMLib.Alvo.Data.Internal;
using MMLib.Alvo.Events;
using System.Linq.Expressions;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps named commands: minimal-API endpoints whose caller, row and access rule Alvo judges before the handler runs.</summary>
/// <remarks>
/// <para>
/// A command is a <c>POST</c>. Before the handler runs, Alvo resolves the caller as the Data API does (no credential is the
/// anonymous caller; a key that cannot be used is 401; a key whose scopes do not cover reading and writing the command's
/// entity — or, for a command bound to none, a wildcard write scope — is 403), answers a replayed <c>Idempotency-Key</c>, reads the
/// bound row as the caller (404 when they cannot read it), and evaluates the access rule (403). The handler then runs
/// outside any transaction: make external calls first, and write in one <c>AlvoCommandContext.TransactAsync</c>.
/// </para>
/// <para>
/// The access rule is CEL over <c>@user</c> and <c>@tenant</c>, and over the row's fields for a command bound to an entity
/// — or a typed lambda translated to that CEL. It is checked when the host boots, with every other extension, and it may not
/// be empty: write <c>'true'</c> to admit every caller explicitly. Map every command before <c>app.Run()</c> and before a
/// descriptor is applied. A key reaches a command bound to an entity only with scopes that both read and write it; the
/// handler's own writes to other entities are judged by the data port's policy as the caller, which API-key scopes do not
/// narrow. A convention that removes Alvo's filter from a command refuses the host's start.
/// </para>
/// <para>
/// A refusal the data port raises inside the handler — forbidden, not found, a failed precondition, a conflict, too deep a
/// chain — is answered as the Data API answers it. Any other exception is the host's: it is answered with Alvo's problem
/// document (500, its message never echoed) only when the host calls <c>AddAlvoProblemDetails()</c> and
/// <c>UseExceptionHandler()</c>, and otherwise with the host's own 500.
/// </para>
/// </remarks>
public static class AlvoCommandEndpointRouteBuilderExtensions
{
    /// <summary>Maps a command bound to no entity, its access rule written as CEL.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name: lower-case segments separated by dots, e.g. <c>quote_stay</c>.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="access">The access rule: CEL over <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler; it may take an <c>AlvoCommandContext</c>.</param>
    /// <returns>The endpoint's builder, for conventions such as <c>WithSummary</c> or <c>RequireRateLimiting</c>.</returns>
    public static RouteHandlerBuilder MapAlvoCommand(this IEndpointRouteBuilder endpoints, string name, string pattern, string access, Delegate handler) =>
        Map(endpoints, name, pattern, null, CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to no entity, its access rule written as a lambda over the caller.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern.</param>
    /// <param name="access">The access rule, e.g. <c>caller =&gt; caller.HasRole("staff")</c>; translated to CEL here.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    /// <exception cref="MMLib.Alvo.Data.AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static RouteHandlerBuilder MapAlvoCommand(
        this IEndpointRouteBuilder endpoints, string name, string pattern, Expression<Func<AlvoConditionCaller, bool>> access, Delegate handler) =>
        Map(endpoints, name, pattern, null, CommandAccess.Typed(access, name), handler);

    /// <summary>Maps a command bound to one row of <paramref name="entity"/>, which its route's <c>{id}</c> names.</summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="entity">The entity the row belongs to.</param>
    /// <param name="access">The access rule: CEL over the row's fields, <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    public static RouteHandlerBuilder MapAlvoCommand(
        this IEndpointRouteBuilder endpoints, string name, string pattern, string entity, string access, Delegate handler) =>
        Map(endpoints, name, pattern, Bound(entity, pattern), CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to one row of <typeparamref name="TEntity"/>'s entity, its access rule written as CEL.</summary>
    /// <typeparam name="TEntity">A typed record carrying <c>[AlvoEntity("…")]</c>.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="access">The access rule: CEL over the row's fields, <c>@user</c> and <c>@tenant</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    public static RouteHandlerBuilder MapAlvoCommand<TEntity>(
        this IEndpointRouteBuilder endpoints, string name, string pattern, string access, Delegate handler)
        where TEntity : class =>
        Map(endpoints, name, pattern, Bound(TypedEntity.NameOf<TEntity>(), pattern), CommandAccess.Of(access), handler);

    /// <summary>Maps a command bound to one row of <typeparamref name="TEntity"/>'s entity, its access rule a typed lambda.</summary>
    /// <typeparam name="TEntity">A typed record carrying <c>[AlvoEntity("…")]</c>.</typeparam>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="name">The command's name.</param>
    /// <param name="pattern">The route pattern; it must have an <c>{id}</c> parameter.</param>
    /// <param name="access">The access rule, e.g. <c>(booking, caller) =&gt; booking.Status == BookingStatus.Requested &amp;&amp; caller.HasRole("staff")</c>.</param>
    /// <param name="handler">The minimal-API handler.</param>
    /// <returns>The endpoint's builder.</returns>
    /// <exception cref="MMLib.Alvo.Data.AlvoTranslationException">The lambda has no CEL equivalent.</exception>
    public static RouteHandlerBuilder MapAlvoCommand<TEntity>(
        this IEndpointRouteBuilder endpoints, string name, string pattern, Expression<Func<TEntity, AlvoConditionCaller, bool>> access, Delegate handler)
        where TEntity : class =>
        Map(endpoints, name, pattern, Bound(TypedEntity.NameOf<TEntity>(), pattern), CommandAccess.Typed(access, name), handler);

    private static string Bound(string entity, string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entity);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return RoutePatternFactory.Parse(pattern).GetParameter("id") is not null
            ? entity
            : throw new ArgumentException(
                $"A command bound to '{entity}' reads the row its route names, so '{pattern}' needs an '{{id}}' route parameter — '/commands/{entity}/{{id:guid}}/confirm'.",
                nameof(pattern));
    }

    private static RouteHandlerBuilder Map(
        IEndpointRouteBuilder endpoints, string name, string pattern, string? entity, CommandAccess access, Delegate handler)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(handler);
        var registry = endpoints.ServiceProvider.GetService<CommandRegistry>()
            ?? throw new InvalidOperationException("Alvo is not registered. Call services.AddAlvo(...) before MapAlvoCommand(...).");
        var command = registry.Map(name, pattern, entity, access);
        var filter = endpoints.ServiceProvider.GetRequiredService<CommandEndpointFilter>();
        var witness = new AlvoGateWitness($"command '{command.Id}'");

        return endpoints.MapPost(pattern, handler)
            .AddEndpointFilter(NoStoreResponseFilter.Instance)
            .AddEndpointFilterFactory(witness.Wrap((factory, next) => filter.For(command, factory, next)))
            .WithMetadata(new CommandEndpointMetadata(command.Id, command.Entity), witness)
            .WithName(CommandOperationIds.Of(command.Id))
            .WithTags(CommandOperationIds.Tag);
    }
}
