using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Api.Internal;

/// <summary>The one instant an HTTP request's every now() reads (HX-11 Ruling 1), bound where Alvo resolves the request's caller.</summary>
internal static class RequestInstant
{
    /// <summary>Binds the request's instant from the host's clock, unless one is already bound on this flow.</summary>
    /// <param name="http">The request.</param>
    /// <returns>A scope that unbinds it when the request's endpoint has run.</returns>
    internal static IDisposable Enter(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return AlvoFlow.EnsureInstant(http.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
    }
}
