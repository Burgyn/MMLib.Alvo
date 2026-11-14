using Microsoft.AspNetCore.Routing;
using MMLib.Alvo.Accounts.Internal;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Maps Alvo's end-user sign-in routes.</summary>
public static class AlvoAccountsEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the sign-in routes under <see cref="MMLib.Alvo.Accounts.AlvoAccountsOptions.Prefix"/>.
    /// </summary>
    /// <remarks>
    /// Requires <c>AddAlvoAccounts</c>, and no pipeline step beyond routing. Today: <c>POST {prefix}/token</c>, signing a
    /// person in by exchanging a provider's ID token (RFC 8693) and refreshing the session (RFC 6749 §6);
    /// <c>GET</c> and <c>PATCH {prefix}/user</c>, the signed-in person and their display name; and
    /// <c>POST {prefix}/logout</c>, ending the bearer's session.
    /// </remarks>
    /// <exception cref="InvalidOperationException"><c>AddAlvoAccounts</c> was not called.</exception>
    /// <param name="endpoints">The route builder to map into.</param>
    /// <returns>The route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapAlvoAccounts(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        AccountsEndpoints.Map(endpoints);
        return endpoints;
    }
}
