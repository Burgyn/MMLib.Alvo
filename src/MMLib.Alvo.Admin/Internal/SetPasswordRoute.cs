using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// Whether this host maps the set-password form's post, <see cref="AlvoAdmin.SetPasswordEndpoint"/> (final branch
/// review, item 15).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the dashboard asks.</b> Redeeming a token needs the identity package, which this package does not reference,
/// so the post is the host's to map: <c>MMLib.Alvo.Host</c> maps it, and an embedded host that registers
/// <c>IAlvoUserAdministration</c> may not. A link to the page there would open a form that posts to a route nobody
/// mapped, where the bare token — which the host's own flow can redeem through <c>AlvoSignIn.SetPasswordAsync</c> —
/// was the whole handover before the page existed.
/// </para>
/// <para>
/// <b>From the endpoints themselves, not an option</b>: a flag an embedding host sets can say "mapped" when it is not,
/// and a new option is public surface for a fact the routing table already holds. Read when asked rather than at
/// registration, because the host maps its endpoints after the container is built; the table is a few hundred entries
/// at most and is read only when a token is issued or copied.
/// </para>
/// </remarks>
/// <param name="services">The host's services, where routing registers its endpoint data source.</param>
internal sealed class SetPasswordRoute(IServiceProvider services)
{
    /// <summary>Whether a <c>POST</c> is mapped at <see cref="AlvoAdmin.SetPasswordEndpoint"/>.</summary>
    public bool IsMapped
        => services.GetService<EndpointDataSource>()?.Endpoints.OfType<RouteEndpoint>().Any(IsTheSetPasswordPost) is true;

    /// <summary>Whether <paramref name="endpoint"/> is a post at the form's action.</summary>
    /// <param name="endpoint">One mapped endpoint.</param>
    private static bool IsTheSetPasswordPost(RouteEndpoint endpoint)
        => string.Equals(
                $"/{endpoint.RoutePattern.RawText?.TrimStart('/')}", AlvoAdmin.SetPasswordEndpoint, StringComparison.OrdinalIgnoreCase)
            && endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods
                .Contains("POST", StringComparer.OrdinalIgnoreCase) is true;
}
