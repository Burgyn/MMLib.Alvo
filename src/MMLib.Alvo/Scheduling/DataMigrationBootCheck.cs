using Microsoft.Extensions.DependencyInjection;

using MMLib.Alvo.Extensions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Scheduling;

/// <summary>
/// The boot's refusal of a data migration that has not run yet, on a scoped schema, in a host with no
/// <see cref="IAlvoTenantCatalog"/>: it would be held forever. A migration that has run needs no tenant list, so its
/// registration never refuses a boot.
/// </summary>
/// <param name="registry">The registered extensions.</param>
/// <param name="services">Tells whether a tenant list is registered.</param>
/// <param name="store">The timer store that records started migrations, or <see langword="null"/> when no provider registered one.</param>
internal sealed class DataMigrationBootCheck(ExtensionRegistry registry, IServiceProviderIsService services, ITimerStore? store)
{
    /// <summary>Refuses the boot when a migration not yet started needs a tenant list on <paramref name="catalog"/>; reads only.</summary>
    /// <param name="catalog">The catalog the boot is about to prime.</param>
    /// <param name="cancellationToken">Cancels the check, failing the boot.</param>
    /// <exception cref="Migrations.AlvoStartupRefusedException">A migration not yet started would be held forever.</exception>
    internal async Task RefuseAsync(PolicyCatalog catalog, CancellationToken cancellationToken)
    {
        registry.Seal();
        if (!ScheduleChecks.NeedsTenants(registry, catalog, services))
        {
            return;
        }

        var started = await StartedAsync(cancellationToken).ConfigureAwait(false);
        List<ExtensionProblem> problems = [.. ScheduleChecks.CheckBoot(registry, catalog, services, started)];
        if (problems.Count > 0)
        {
            throw ExtensionRefusal.Of(problems);
        }
    }

    private async Task<HashSet<string>> StartedAsync(CancellationToken ct)
    {
        if (store is null)
        {
            return [];
        }

        await store.EnsureAsync(ct).ConfigureAwait(false);
        return (await store.StartedMigrationsAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
    }
}
