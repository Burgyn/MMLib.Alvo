using MMLib.Alvo.Api.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Lets a host hand Alvo the rendering of an unhandled failure — the standalone host's decision, and an
/// embedded host's to decline.
/// </summary>
public static class AlvoProblemDetailsExtensions
{
    /// <summary>
    /// Registers the exception handler that logs an unhandled failure and answers it with
    /// <c>https://alvo.dev/errors/internal</c>. Pair it with <c>app.UseExceptionHandler()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and deliberately not part of <c>AddAlvo</c>.</b> In embedded mode the host owns its error
    /// rendering and Alvo not stealing the exception is the point; in standalone mode Alvo <em>is</em> the
    /// pipeline and nothing else can answer. One registration, two correct behaviours (#119).
    /// </para>
    /// <para>
    /// <c>AddProblemDetails()</c> is registered alongside because <c>UseExceptionHandler()</c> refuses to
    /// configure a middleware with neither a handler path nor a problem-details service to fall back to. The
    /// fallback is unreachable while this handler is registered — it answers every exception and returns
    /// <see langword="true"/> — so the framework's own writer never renders an Alvo response.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddAlvoProblemDetails(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails();
        services.AddExceptionHandler<AlvoExceptionHandler>();
        return services;
    }
}
