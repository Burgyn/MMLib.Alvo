using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Management.Internal;

namespace MMLib.Alvo.Management;

/// <summary>Attaches the management gate to a route.</summary>
internal static class ManagementAccessRouteBuilderExtensions
{
    /// <summary>
    /// Requires that the caller's resolved <see cref="ManagementLevel"/> reaches
    /// <paramref name="operation"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every management route carries this. A route without the gate and an operation without a route are
    /// the two ways this surface can quietly open; the Management API owes the second half — a fact that
    /// answers <c>403</c> over <em>every</em> management route — on the issue that adds the routes.
    /// </para>
    /// <para>
    /// A filter <em>factory</em> rather than a constructed instance, so the evaluator and the caller
    /// accessor are resolved from the application's own container at route-building time and this
    /// extension does not become a second place their lifetimes are decided.
    /// </para>
    /// </remarks>
    /// <param name="builder">The route being built.</param>
    /// <param name="operation">The operation that route performs.</param>
    /// <returns><paramref name="builder"/>, for chaining.</returns>
    internal static RouteHandlerBuilder RequireAlvoManagementAccess(
        this RouteHandlerBuilder builder, ManagementOperation operation)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.AddEndpointFilterFactory((factoryContext, next) =>
        {
            var services = factoryContext.ApplicationServices;
            var filter = new ManagementAccessEndpointFilter(
                operation,
                services.GetRequiredService<ManagementAccessEvaluator>(),
                services.GetRequiredService<IAlvoContextAccessor>());

            return invocation => filter.InvokeAsync(invocation, next);
        });
    }
}
