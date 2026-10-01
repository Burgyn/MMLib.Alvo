using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Admin.Components;

namespace MMLib.Alvo.Admin;

/// <summary>
/// Maps the dashboard.
/// </summary>
public static class AlvoAdminEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the dashboard's components, unless <see cref="AlvoAdminOptions.Enabled"/> says not to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller is responsible for three pieces of pipeline this cannot add for it, because all
    /// three are middleware and middleware is ordered by the host rather than by an endpoint:
    /// <c>UseStaticFiles</c> or <c>MapStaticAssets</c> (the design system travels as a static web
    /// asset of this package), <c>UseAntiforgery</c> (Blazor's form handling requires it), and
    /// authentication. <c>MMLib.Alvo.Host</c> does all three.
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The route builder to map into.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAlvoAdmin(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<AlvoAdminOptions>>().Value;
        if (!options.Enabled)
        {
            return endpoints;
        }

        endpoints.MapRazorComponents<AdminApp>().AddInteractiveServerRenderMode();

        return endpoints;
    }
}
