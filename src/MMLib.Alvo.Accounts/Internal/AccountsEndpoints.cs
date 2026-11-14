using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MMLib.Alvo.Api.Internal;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>The sign-in routes, mapped under <see cref="AlvoAccountsOptions.Prefix"/> and tagged <see cref="Tag"/>.</summary>
/// <remarks>
/// Every route carries the core's CORS filter first (spec §7.3, S2 D3), and the group answers preflights when origins are
/// configured; the token endpoint is charged the <see cref="AlvoRateLimit.Token"/> budget after <see cref="NoStore"/>, so a
/// 429 is uncacheable and readable from an allowed origin.
/// </remarks>
internal static class AccountsEndpoints
{
    /// <summary>The OpenAPI tag every sign-in route carries.</summary>
    internal const string Tag = "auth";

    /// <summary>The operation id of the token endpoint.</summary>
    internal const string TokenOperationId = "auth.token";

    /// <summary>Maps the sign-in routes.</summary>
    /// <param name="endpoints">The route builder to map into.</param>
    /// <returns>The group the routes are mapped in.</returns>
    /// <exception cref="InvalidOperationException"><c>AddAlvoAccounts</c> was not called.</exception>
    internal static RouteGroupBuilder Map(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.ServiceProvider.GetService<TokenEndpoint>() is null)
        {
            throw new InvalidOperationException(
                "End-user sign-in is not registered. Call services.AddAlvoAccounts(...) before MapAlvoAccounts().");
        }

        var services = endpoints.ServiceProvider;
        var prefix = services.GetRequiredService<IOptions<AlvoAccountsOptions>>().Value.Prefix;
        var cors = services.GetRequiredService<AlvoCors>();
        var group = endpoints.MapGroup(prefix).WithTags(Tag);
        group.MapPost("/token", static (HttpContext http, TokenEndpoint token) => token.HandleAsync(http))
            .AddEndpointFilter(cors.Filter)
            .AddEndpointFilter(NoStore)
            .AddEndpointFilter(services.GetRequiredService<AlvoRateLimits>().For(AlvoRateLimit.Token))
            .WithName(TokenOperationId);
        UserEndpoints.Map(group, cors);
        DiscoveryEndpoints.Map(endpoints, group, prefix, cors);
        cors.MapPreflight(group, string.Empty);
        return group;
    }

    /// <summary>
    /// Marks every answer <c>Cache-Control: no-store</c> and <c>Pragma: no-cache</c> (RFC 6749 §5.1) — set before the
    /// handler runs, so a refusal carries them as surely as a token does.
    /// </summary>
    /// <param name="context">The invocation.</param>
    /// <param name="next">The handler.</param>
    internal static ValueTask<object?> NoStore(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var headers = context.HttpContext.Response.Headers;
        headers.CacheControl = CacheControlHeaderValue.NoStoreString;
        headers.Pragma = CacheControlHeaderValue.NoCacheString;
        return next(context);
    }
}
