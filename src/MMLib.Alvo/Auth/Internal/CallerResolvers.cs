using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// The resolvers a request's credential can be handed to: the API-key resolver always, and the person resolver
/// only when a host registered one under <see cref="CallerResolution.PersonResolverKey"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>"Registered" is asked once, of the container, and a container that cannot answer counts as no.</b>
/// <see cref="IServiceProviderIsKeyedService"/> is the question Microsoft's container answers without building
/// anything; a third-party container that does not offer it leaves the Bearer channel closed — today's behaviour,
/// so a host's own <c>Authorization</c> header is never taken over on a guess.
/// </para>
/// <para>
/// <b>Only a registration under exactly that key counts.</b> The container answers <see langword="true"/> for any
/// key when a resolver was registered under <see cref="KeyedService.AnyKey"/>, so the same question is asked of a
/// key nobody else holds: a container that says yes to that one holds a catch-all, not a person resolver, and the
/// Bearer channel stays closed — a host's catch-all must not take over its own <c>Authorization</c> header.
/// </para>
/// <para>
/// <b>The person resolver itself is resolved per request</b>, from <see cref="HttpContext.RequestServices"/>, so
/// a scoped one works — the precedent <c>AlvoIdentity.ResolverKey</c>'s keyed-scoped resolver set.
/// </para>
/// </remarks>
/// <param name="apiKeys">The unkeyed resolver an API key is handed to.</param>
/// <param name="services">The root container, asked once whether a person resolver is registered.</param>
internal sealed class CallerResolvers(IAlvoContextResolver apiKeys, IServiceProvider services)
{
    /// <summary>A key no registration can name, the probe that tells a catch-all from a person resolver.</summary>
    private static readonly object _unnamedKey = new();

    /// <summary>Gets the resolver an API key is handed to.</summary>
    internal IAlvoContextResolver ApiKeys { get; } = apiKeys;

    /// <summary>Gets whether a person resolver is registered, which is what opens the Bearer channel.</summary>
    internal bool HasPerson { get; } = IsPersonRegistered(services);

    /// <summary>The person resolver, from the request's own scope.</summary>
    /// <remarks>
    /// Asked only after <see cref="HasPerson"/> opened the Bearer channel; asked otherwise, it refuses rather than
    /// reach past the answer the container gave at startup.
    /// </remarks>
    /// <param name="http">The request whose services resolve it.</param>
    internal IAlvoContextResolver Person(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return HasPerson
            ? http.RequestServices.GetRequiredKeyedService<IAlvoContextResolver>(CallerResolution.PersonResolverKey)
            : throw new InvalidOperationException("No person resolver is registered, so the Bearer channel is closed.");
    }

    /// <summary>
    /// Whether <paramref name="services"/> holds a resolver under exactly the person key — the answer
    /// <see cref="HasPerson"/> gives, asked without activating the API-key resolver (the boot plan asks it too).
    /// </summary>
    /// <param name="services">The root container.</param>
    internal static bool IsPersonRegistered(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return IsPersonRegistered(services.GetService<IServiceProviderIsKeyedService>());
    }

    /// <summary>Whether a resolver is registered under exactly the person key, not merely answered for it.</summary>
    /// <param name="keyed">The container's keyed-service question, or <see langword="null"/> when it cannot answer.</param>
    private static bool IsPersonRegistered(IServiceProviderIsKeyedService? keyed) =>
        keyed is not null
        && keyed.IsKeyedService(typeof(IAlvoContextResolver), CallerResolution.PersonResolverKey)
        && !keyed.IsKeyedService(typeof(IAlvoContextResolver), _unnamedKey);
}
