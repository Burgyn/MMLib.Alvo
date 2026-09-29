using Microsoft.AspNetCore.Http;

namespace MMLib.Alvo.Host.Internal;

/// <summary>The answer a credential form's post gets: a <c>303</c> to a page, never kept and never referred.</summary>
/// <remarks>
/// <para>
/// <b>303, not 302</b>: the answer to a post is a page to <c>GET</c>, which is what 303 says and 302 only implies. A
/// browser treats them alike; the status is for whoever reads the exchange.
/// </para>
/// <para>
/// <b><c>no-store</c> and <c>no-referrer</c></b> on every such answer (design §1): a redirect that carries a
/// fragment back to the set-password page is not something a cache should keep, and the page it lands on must not
/// hand its address to anything it links to. The path base is prefixed, so the redirect stays under a host mounted
/// below the root.
/// </para>
/// </remarks>
internal static class AlvoAdminRedirect
{
    /// <summary>Sends the browser to <paramref name="location"/>.</summary>
    /// <param name="http">The request being answered.</param>
    /// <param name="location">A local path, with any query and fragment.</param>
    /// <returns>The result to return from the handler.</returns>
    internal static IResult SeeOther(HttpContext http, string location)
    {
        http.Response.StatusCode = StatusCodes.Status303SeeOther;
        http.Response.Headers.Location = $"{http.Request.PathBase}{location}";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["Referrer-Policy"] = "no-referrer";
        return Results.Empty;
    }
}
