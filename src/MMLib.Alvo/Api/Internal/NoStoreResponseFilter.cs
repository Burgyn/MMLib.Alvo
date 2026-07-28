using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Marks every response a generated endpoint produces <c>Cache-Control: no-store</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It is what pays for the strong entity tag.</b> <see cref="RowVersionETag"/> is minted over the row
/// <em>version</em>, not over the response bytes, so two callers whose policies mask different fields share
/// one tag for one row version while their representations differ. Under RFC 9110 §13.1.1 that is the only
/// tag an <c>If-Match</c> could ever match — but it would also let a shared cache serve one caller's
/// policy-masked representation to the next caller who presents the same tag. <c>no-store</c> is the header
/// that makes the cost of the trade unrealizable rather than merely unlikely: these responses are private
/// per caller, and an intermediary may not keep them at all.
/// </para>
/// <para>
/// <b>An endpoint filter, and the <em>first</em> one, so it also covers what the pipeline refuses.</b>
/// Filters run in the order they are added, so this one wraps <see cref="AlvoContextFilter"/> and stamps the
/// 401 and 403 that filter short-circuits with, not only the responses the delegate reaches. It is attached
/// in <c>DataApiEndpoints.Protect</c> — the one place a route is fused to its gate — so a sixth endpoint
/// added later cannot be mapped without it.
/// </para>
/// <para>
/// The header is written before the delegate runs rather than after: a response whose body has already
/// started cannot take a new header, and an <see cref="IResult"/> deeper in the pipeline is free to begin
/// writing whenever it likes.
/// </para>
/// <para>
/// Stateless, so one instance serves every endpoint — see <see cref="Instance"/>.
/// </para>
/// </remarks>
internal sealed class NoStoreResponseFilter : IEndpointFilter
{
    /// <summary>The one instance every generated endpoint shares.</summary>
    /// <remarks>
    /// A singleton rather than one per route: the filter holds no per-endpoint state, and a fresh instance
    /// per entity and operation would allocate one object per mapped route for nothing.
    /// </remarks>
    internal static NoStoreResponseFilter Instance { get; } = new();

    private NoStoreResponseFilter()
    {
    }

    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        context.HttpContext.Response.Headers.CacheControl = CacheControlHeaderValue.NoStoreString;
        return next(context);
    }
}
