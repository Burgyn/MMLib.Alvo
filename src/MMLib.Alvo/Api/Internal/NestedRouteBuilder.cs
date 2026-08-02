using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The smallest <see cref="IEndpointRouteBuilder"/> that the real minimal-API <c>Map*</c> helpers can be
/// called on off-application — so <see cref="AlvoEndpointDataSource"/> can build its endpoints the way a host
/// builds one, rather than assembling them by hand.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because of a measured fact, not for tidiness.</b> Endpoints assembled by hand with a
/// <c>RouteEndpointBuilder</c> route correctly and are <em>invisible to ApiExplorer</em>, so the OpenAPI
/// document loses every path built that way while every routing test stays green (design fact 4). Calling
/// <c>MapGet</c>/<c>MapPost</c>/… on this builder puts the framework's own
/// <c>RequestDelegateFactory</c> in the path, which is what produces the metadata the document is generated
/// from (design fact 5).
/// </para>
/// <para>
/// It holds its own <see cref="DataSources"/> rather than writing into the host's, which is the whole point:
/// the endpoints the <c>Map*</c> calls produce end up in <em>this</em> collection, for
/// <see cref="AlvoEndpointDataSource"/> to flatten and publish when it is enumerated — not in the host's
/// endpoint table at the moment <c>MapAlvoDataApi</c> was called.
/// </para>
/// </remarks>
/// <param name="services">The application's services, which the mapped delegates resolve their arguments from.</param>
internal sealed class NestedRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
{
    /// <inheritdoc/>
    public IServiceProvider ServiceProvider { get; } = services;

    /// <inheritdoc/>
    public ICollection<EndpointDataSource> DataSources { get; } = [];

    /// <inheritdoc/>
    /// <remarks>
    /// Nothing in Alvo's mapping builds middleware, so this exists only to satisfy the interface for a
    /// <c>Map*</c> overload that would. It is an ordinary
    /// <see cref="ApplicationBuilder"/> over the application's services, exactly as the host's own builder
    /// hands out.
    /// </remarks>
    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
}
