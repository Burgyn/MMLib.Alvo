using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Rules.Internal;
using MMLib.Alvo.Schema;

namespace MMLib.Alvo.Rules;

/// <summary>Registers <see cref="IPolicyEngine"/>: the default-deny authorization checkpoint every data port consults.</summary>
internal static class RulesSetup
{
    /// <summary>
    /// Adds <see cref="IPolicyCatalogProvider"/> (a single mutable holder, unprimed until a descriptor
    /// is actually applied — see <see cref="PolicyCatalogPriming"/>, called from
    /// <c>RuntimeSchemaService</c> and the code-first startup path) and <see cref="IPolicyEngine"/>
    /// as singletons. Registering the provider unprimed, rather than eagerly building a catalog here,
    /// is deliberate: a descriptor is not available when <c>AddAlvo</c> runs (the <c>FromDescriptor</c>
    /// chicken/egg <c>AlvoServiceCollectionExtensions</c> already names), and resolving
    /// <see cref="IPolicyEngine"/> must never itself fail or block — only an
    /// <see cref="IPolicyEngine.Resolve"/> call made before anything has been applied denies, with a
    /// message that says exactly that.
    /// <para>
    /// <see cref="IBeforeHookRunner"/> is registered here and not beside the data path because it reads the
    /// <em>same</em> catalog instance <see cref="IPolicyEngine"/> resolves against: a driver calls it inside
    /// the transaction it opened, and the hooks it runs have to come from the apply that compiled the rules
    /// judging that same write.
    /// </para>
    /// </summary>
    /// <remarks>
    /// <see cref="IRoleCatalogProvider"/> resolves to the <em>same instance</em> as
    /// <see cref="IPolicyCatalogProvider"/> rather than to a second registration of the concrete
    /// type: two independently primed holders could serve authentication a role set the rules were
    /// never compiled against, and a host replacing the policy catalog provider must not silently
    /// keep the default one's roles. Registered with <c>TryAddSingleton</c>, so a host with an
    /// external identity source (OIDC groups, a directory — #36) registers its own role provider and
    /// takes identity roles over without touching the policy catalog.
    /// <see cref="ISchemaRegistry"/> is registered the same way and for the same reason: the applied
    /// schema a data port validates a caller's field names against must be the very one the rules that
    /// judge the same request were compiled against, which a second independently primed holder cannot
    /// guarantee.
    /// </remarks>
    /// <param name="services">The service collection to add the rules services to.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    internal static IServiceCollection AddAlvoRules(this IServiceCollection services)
    {
        services.TryAddSingleton<IPolicyCatalogProvider, PolicyCatalogProvider>();
        services.TryAddSingleton<IRoleCatalogProvider>(
            provider => provider.GetRequiredService<IPolicyCatalogProvider>());
        services.TryAddSingleton<ISchemaRegistry>(
            provider => provider.GetRequiredService<IPolicyCatalogProvider>());
        services.TryAddSingleton<IPolicyEngine, PolicyEngine>();
        services.TryAddSingleton<IBeforeHookRunner, BeforeHookRunner>();
        return services;
    }
}
