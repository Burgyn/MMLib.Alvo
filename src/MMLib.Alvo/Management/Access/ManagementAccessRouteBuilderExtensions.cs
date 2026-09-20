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
    /// Every management route carries this, and no route depends on it alone: <c>AlvoManagementService</c>
    /// asks the same question through the same evaluator at the head of every contract member, so a route
    /// mapped without the gate is still refused — it merely pays for model binding first. What would open
    /// this surface quietly is a route that reaches no contract member at all, which is what
    /// <c>ManagementAccessTests.Every_mapped_management_route_refuses_a_caller_the_project_names_nowhere</c>
    /// sweeps the live route table for.
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
