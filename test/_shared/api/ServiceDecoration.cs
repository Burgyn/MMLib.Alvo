using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Api.Tests;

/// <summary>
/// Wraps a service Alvo has already registered, so a fact can count what the production wiring does to it
/// without changing that wiring.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this cannot be done through <c>AlvoApiWorldSetup.ConfigureServices</c>.</b> That hook runs
/// <em>before</em> <c>AddAlvo</c>, and every Alvo registration is a <c>TryAdd</c>. Registering a decorator
/// there therefore wins the slot and Alvo's own implementation is never registered at all — leaving the
/// decorator with nothing to wrap. Decoration is inherently a thing you do <em>after</em> the registration
/// it decorates, which is what <c>ConfigureServicesAfterAlvo</c> exists for.
/// </para>
/// <para>
/// <b>Why the descriptor is re-created rather than resolved.</b> Taking the inner instance from a built
/// provider would build a second container; instead the existing <see cref="ServiceDescriptor"/> is removed
/// and replaced by a factory that instantiates whatever it described — a type, a factory, or an instance —
/// and hands it to <paramref name="decorate"/>. So the inner object is the same one production would have
/// used, constructed by the same container, and a registration this helper does not understand throws
/// rather than being silently skipped.
/// </para>
/// <para>
/// <b>Decorating one interface is enough when the others forward to it.</b> <c>Rules/Setup.cs</c> registers
/// <c>ISchemaRegistry</c> and <c>IRoleCatalogProvider</c> as factories that resolve
/// <c>IPolicyCatalogProvider</c>, because two independently primed holders of the same catalog is a defect
/// its own remarks describe. Replacing the <c>IPolicyCatalogProvider</c> descriptor therefore redirects all
/// three, and the identity the security core deliberately shares stays shared. Registering a second
/// decorator for <c>ISchemaRegistry</c> would break exactly that.
/// </para>
/// </remarks>
internal static class ServiceDecoration
{
    /// <summary>Replaces <typeparamref name="TService"/>'s registration with <paramref name="decorate"/> applied to it.</summary>
    /// <typeparam name="TService">The already-registered service to wrap.</typeparam>
    /// <param name="services">The collection Alvo has already registered into.</param>
    /// <param name="decorate">Builds the wrapper around the instance the existing registration produces.</param>
    /// <exception cref="InvalidOperationException">
    /// Nothing registered <typeparamref name="TService"/>, or it was registered more than once. Both mean
    /// the assumption this helper rests on has moved, and a fact that silently measured an undecorated
    /// service would pass while measuring nothing.
    /// </exception>
    internal static void Decorate<TService>(
        this IServiceCollection services, Func<TService, TService> decorate)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(decorate);

        var existing = Sole<TService>(services);
        services.Remove(existing);
        services.AddSingleton(provider => decorate((TService)Instantiate(existing, provider)));
    }

    private static ServiceDescriptor Sole<TService>(IServiceCollection services)
    {
        var matches = services.Where(descriptor => descriptor.ServiceType == typeof(TService)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one registration of {typeof(TService).Name} to decorate, found "
                + $"{matches.Count}. Either AddAlvo has not run yet, or the registration moved.");
    }

    private static object Instantiate(ServiceDescriptor descriptor, IServiceProvider provider) =>
        descriptor.ImplementationInstance
        ?? descriptor.ImplementationFactory?.Invoke(provider)
        ?? ActivatorUtilities.CreateInstance(
            provider,
            descriptor.ImplementationType
            ?? throw new InvalidOperationException(
                $"{descriptor.ServiceType.Name} is registered in a form this helper cannot instantiate."));
}
