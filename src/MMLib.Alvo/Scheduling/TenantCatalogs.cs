using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// Which <see cref="IAlvoTenantCatalog"/> lists this host's tenants under a descriptor (spec §6.6, S3 plan D13): the host's
/// own when it registers one; else the self-service registry's, while the descriptor declares <c>tenancy.selfService</c>
/// and a tenant registry is registered — listing only the tenants that descriptor honours (<see cref="RegistryTenantCatalog"/>);
/// else none.
/// </summary>
/// <remarks>
/// <para>
/// <b>Resolved, not registered.</b> The descriptor is applied at run time, so "a default only when <c>selfService</c> is
/// on and the host registered none" cannot be a registration: every "is there a tenant list" question asks
/// <see cref="Lists(DescriptorHost, bool)"/> with the descriptor it is judging, and every listing goes through
/// <see cref="Current"/> or <see cref="For"/>. A per-tenant schedule in a host with no catalog of its own is therefore still
/// refused wherever the descriptor has no <c>selfService</c>.
/// </para>
/// <para>
/// <b>The host's catalog always wins</b>: it may know tenants the registry does not (API-key tenants are never in it).
/// </para>
/// </remarks>
/// <param name="services">Resolves the host's catalog and the tenant registry, each once.</param>
/// <param name="catalogs">The applied policy catalog, whose self-service policy decides <see cref="Current"/>.</param>
internal sealed class TenantCatalogs(IServiceProvider services, IPolicyCatalogProvider catalogs)
{
    private readonly Lazy<IAlvoTenantCatalog?> _host = new(() => services.GetService<IAlvoTenantCatalog>());

    private readonly Lazy<ITenantStore?> _registry = new(() => services.GetService<ITenantStore>());

    /// <summary>Gets the tenant list under the applied descriptor, or <see langword="null"/> when there is none.</summary>
    internal IAlvoTenantCatalog? Current => For(catalogs.Current?.SelfService ?? SelfServicePolicy.None);

    /// <summary>The tenant list under a descriptor whose self-service policy is <paramref name="policy"/>.</summary>
    /// <param name="policy">The self-service policy of the descriptor in question; <see cref="SelfServicePolicy.None"/> without <c>selfService</c>.</param>
    internal IAlvoTenantCatalog? For(SelfServicePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return _host.Value ?? (policy.Enabled && _registry.Value is { } store ? new RegistryTenantCatalog(store, policy) : null);
    }

    /// <summary>Whether <paramref name="host"/> lists tenants under a descriptor that does or does not declare <c>selfService</c>.</summary>
    /// <param name="host">The host facts a descriptor compiles against.</param>
    /// <param name="selfService">Whether that descriptor declares <c>tenancy.selfService</c>.</param>
    internal static bool Lists(DescriptorHost host, bool selfService)
    {
        ArgumentNullException.ThrowIfNull(host);
        return Lists(host.HasTenantCatalog, host.HasTenantRegistry, selfService);
    }

    /// <summary>Whether the container <paramref name="services"/> describes lists tenants under such a descriptor.</summary>
    /// <param name="services">Answers which services the container can resolve.</param>
    /// <param name="selfService">Whether that descriptor declares <c>tenancy.selfService</c>.</param>
    internal static bool Lists(IServiceProviderIsService services, bool selfService)
    {
        ArgumentNullException.ThrowIfNull(services);
        return Lists(HostCatalog(services), Registry(services), selfService);
    }

    /// <summary>Whether the host registers a tenant list of its own.</summary>
    internal static bool HostCatalog(IServiceProviderIsService services) => services.IsService(typeof(IAlvoTenantCatalog));

    /// <summary>Whether a self-service tenant registry is registered.</summary>
    internal static bool Registry(IServiceProviderIsService services) => services.IsService(typeof(ITenantStore));

    private static bool Lists(bool hostCatalog, bool registry, bool selfService) => hostCatalog || (registry && selfService);
}
