using MMLib.Alvo.Api.Internal;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Lets a host hand Alvo the rendering of an unhandled failure — the standalone host's decision, and an
/// embedded host's to decline.
/// </summary>
public static class AlvoProblemDetailsExtensions
{
    /// <summary>
    /// Registers the exception handler that answers an unhandled failure <b>from one of Alvo's own
    /// endpoints</b> with Alvo's problem document. Pair it with <c>app.UseExceptionHandler()</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Opt-in, and deliberately not part of <c>AddAlvo</c>.</b> In embedded mode the host owns its error
    /// rendering and Alvo not stealing the exception is the point; in standalone mode Alvo <em>is</em> the
    /// pipeline and nothing else can answer. One registration, two correct behaviours (#119).
    /// </para>
    /// <para>
    /// <b>The scope is Alvo's generated routes, not the pipeline.</b> The handler declines a failure from any
    /// other endpoint, so an <c>IExceptionHandler</c> a host registers <em>after</em> this call still runs for
    /// the host's own endpoints — the framework stops at the first handler that claims a failure, and a
    /// version of this that claimed all of them silently deleted the host's error contract from its own 500s.
    /// A host that wants Alvo's document everywhere therefore does not get it, and that is the trade: an
    /// embedded host owning its rendering is the whole point of the opt-in.
    /// </para>
    /// <para>
    /// <c>AddProblemDetails()</c> is registered alongside because <c>UseExceptionHandler()</c> refuses to
    /// configure a middleware with neither a handler path nor a problem-details service to fall back to. It is
    /// a real fallback rather than a formality: it is what answers a failure this handler declines and no host
    /// handler claims.
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
