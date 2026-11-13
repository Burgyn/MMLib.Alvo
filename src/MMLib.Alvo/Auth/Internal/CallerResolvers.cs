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
/// <b>The person resolver itself is resolved per request</b>, from <see cref="HttpContext.RequestServices"/>, so
/// a scoped one works — the precedent <c>AlvoIdentity.ResolverKey</c>'s keyed-scoped resolver set.
/// </para>
/// </remarks>
/// <param name="apiKeys">The unkeyed resolver an API key is handed to.</param>
/// <param name="services">The root container, asked once whether a person resolver is registered.</param>
internal sealed class CallerResolvers(IAlvoContextResolver apiKeys, IServiceProvider services)
{
    /// <summary>Gets the resolver an API key is handed to.</summary>
    internal IAlvoContextResolver ApiKeys { get; } = apiKeys;

    /// <summary>Gets whether a person resolver is registered, which is what opens the Bearer channel.</summary>
    internal bool HasPerson { get; } =
        services.GetService<IServiceProviderIsKeyedService>()
            ?.IsKeyedService(typeof(IAlvoContextResolver), CallerResolution.PersonResolverKey) ?? false;

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
}
