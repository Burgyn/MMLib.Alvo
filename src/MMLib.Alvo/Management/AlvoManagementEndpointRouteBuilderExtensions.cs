using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Management;
using MMLib.Alvo.Management.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The Management API's endpoint seam, owned by the core package and deliberately separate from the DI seam
/// (<c>docs/architecture/extensibility.md</c> rule 10): registering Alvo never exposes an endpoint.
/// </summary>
public static class AlvoManagementEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps one minimal-API delegate per <see cref="IAlvoManagement"/> member under
    /// <see cref="AlvoManagementOptions.RoutePrefix"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Nothing mapped here is reachable without an explicit grant.</b> Every route carries the gate the
    /// descriptor's <c>access</c> block compiles, and a caller that block does not name is refused — as is
    /// a caller presenting no credential at all. A project with no <c>access</c> block therefore admits
    /// nobody but the deployment's bootstrap administrator, which is what default-deny means here.
    /// </para>
    /// <para>
    /// <b>It is not part of <c>MapAlvo()</c>.</b> That call is the HTTP surface every host gets from the
    /// framework — the probes and the generated Data API — and mounting an administration surface as a side
    /// effect of mapping a data one is a decision an embedded host has to make for itself.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder to map onto.</param>
    /// <returns>A convention builder over the management routes, and over nothing else.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="endpoints"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">Alvo is not registered in the application's services.</exception>
    public static IEndpointConventionBuilder MapAlvoManagementApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var services = endpoints.ServiceProvider;
        if (services.GetService<IAlvoManagement>() is null)
        {
            throw new InvalidOperationException(
                "The Alvo Management API is not registered. Call services.AddAlvo(...) before "
                + "MapAlvoManagementApi().");
        }

        return ManagementEndpoints.Map(
            endpoints, services.GetRequiredService<IOptions<AlvoManagementOptions>>().Value);
    }
}
