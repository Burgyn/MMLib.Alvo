using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Data;
using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Functions.Internal;
using MMLib.Alvo.Scheduling;

namespace MMLib.Alvo.Rules.Internal;

/// <summary>
/// The host facts a descriptor's actions compile against: the functions it registers, whether it lists tenants or keeps a
/// self-service tenant registry that lists them under <c>tenancy.selfService</c>, and whether
/// it keeps a command ledger an <c>entity.update</c> commits its key in. <see cref="Empty"/> refuses every function action
/// and every <c>entity.update</c> — fail closed.
/// </summary>
/// <param name="Functions">The registered functions, by name.</param>
/// <param name="HasTenantCatalog">Whether the host registers an <c>IAlvoTenantCatalog</c> of its own.</param>
/// <param name="HasActionLedger">Whether a command ledger and the transaction unit it commits in are both registered.</param>
internal sealed record DescriptorHost(
    IReadOnlyDictionary<string, FunctionRegistration> Functions, bool HasTenantCatalog, bool HasActionLedger)
{
    /// <summary>
    /// Gets whether a self-service tenant registry is registered — a tenant list only under a descriptor that declares
    /// <c>tenancy.selfService</c>; ask <see cref="TenantCatalogs.Lists(DescriptorHost, bool)"/>, never this alone.
    /// </summary>
    internal bool HasTenantRegistry { get; init; }

    /// <summary>A host that registers nothing: what a catalog built outside a container compiles against.</summary>
    internal static DescriptorHost Empty { get; } =
        new(new Dictionary<string, FunctionRegistration>(StringComparer.Ordinal), HasTenantCatalog: false, HasActionLedger: false);

    /// <summary>The facts of the host <paramref name="registry"/> and <paramref name="services"/> describe.</summary>
    /// <param name="registry">The host's extension registrations.</param>
    /// <param name="services">Answers which services the container can resolve.</param>
    internal static DescriptorHost From(ExtensionRegistry registry, IServiceProviderIsService services)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(services);

        return new(
            registry.Functions.ToDictionary(function => function.Id, StringComparer.Ordinal),
            TenantCatalogs.HostCatalog(services),
            services.IsService(typeof(ICommandLedger)) && services.IsService(typeof(IAlvoTransactions)))
        {
            HasTenantRegistry = TenantCatalogs.Registry(services),
        };
    }
}
