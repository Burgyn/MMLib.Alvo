using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MMLib.Alvo.Admin.Internal;

namespace MMLib.Alvo.Admin;

/// <summary>
/// Registers the dashboard.
/// </summary>
public static class AlvoAdminServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Razor components the dashboard is built from, and the server-interactive render
    /// mode they run in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Server-interactive, not WebAssembly, and the reason is the contract.</b> The dashboard
    /// reads and writes through <c>IAlvoManagement</c>, resolved from DI in the same process
    /// (design §1.2, <i>one application service, two transports</i>). A WebAssembly dashboard could
    /// not do that — it would have to reach its own process over HTTP, which is the second
    /// transport, and would then need the whole Management API surface to be reachable from a
    /// browser origin before the first screen rendered.
    /// </para>
    /// <para>
    /// Nothing else is registered here. The dashboard has no service of its own: it holds
    /// <c>IAlvoManagement</c> and the ports in <c>MMLib.Alvo.Abstractions</c>, and a screen that
    /// needed a service of its own would be a screen doing work the core should be doing.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="configure">Configures the dashboard.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddAlvoAdmin(
        this IServiceCollection services, Action<AlvoAdminOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<AlvoAdminOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.AddRazorComponents().AddInteractiveServerComponents();
        services.AddCascadingAuthenticationState();

        /* Scoped: in Blazor Server a scope is a circuit, so one operator's session holds one
           gateway and its cache never crosses to another's. Its remarks argue why that cache is a
           correctness property rather than a speed one. */
        services.TryAddScoped<ManagementGateway>();

        return services;
    }
}
