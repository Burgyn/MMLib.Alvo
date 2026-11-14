using Microsoft.AspNetCore.Routing;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps Alvo's end-user sign-in routes.</summary>
public static class AlvoAccountsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the sign-in routes under <see cref="MMLib.Alvo.Accounts.AlvoAccountsOptions.Prefix"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddAlvoAccounts</c>, and no pipeline step beyond routing.
    /// </remarks>
    /// <param name="endpoints">The route builder to map into.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAlvoAccounts(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        return endpoints;
    }
}
