using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Turns a management request into a resolved caller and publishes them, so the gate that runs next has
/// someone to judge.
/// </summary>
/// <remarks>
/// <para>
/// <b>It authenticates; it authorizes nothing.</b> <see cref="ManagementAccessEndpointFilter"/> reads
/// <see cref="IAlvoContextAccessor.Principal"/> and answers the 403, and it is the only thing that does —
/// two filters deciding admission would be the divergence the one-path contract exists to prevent.
/// </para>
/// <para>
/// <b>The scope gate is deliberately not applied here</b>, unlike on a Data API route. A key's scopes are
/// <c>&lt;entity&gt;:&lt;read|write&gt;</c> and the management surface is not an entity, so there is no
/// scope for it to narrow; what a caller may do to a project's configuration is the descriptor's
/// <c>access</c> block, evaluated behind the gate. Applying <see cref="ScopeGate"/> here would need an
/// entity name this layer would have to invent.
/// </para>
/// <para>
/// <b>Three outcomes, the same three the Data API's filter has.</b> No credential at all is anonymous
/// rather than 401 — the gate refuses an anonymous caller through the same default-deny path a
/// credentialled caller who matches no level takes. A credential that was presented and cannot be used is
/// 401, before the gate is consulted: the caller believes they hold a credential and do not, which is a
/// different fix from an access block that does not name them.
/// </para>
/// <para>
/// The caller is taken away again in a <c>finally</c>, on <see cref="AlvoContextFilter"/>'s precedent: a
/// throwing endpoint must not leave a caller published on the ambient context this request's thread later
/// reuses.
/// </para>
/// </remarks>
/// <param name="resolver">Resolves a presented credential into a principal.</param>
/// <param name="accessor">Where the resolved caller is published for the gate and the delegate.</param>
/// <param name="authOptions">Carries the header names a credential and a requested tenant are read from.</param>
internal sealed class ManagementCallerFilter(
    IAlvoContextResolver resolver,
    IAlvoContextAccessor accessor,
    IOptions<AlvoAuthOptions> authOptions) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var options = authOptions.Value;
        var presented = Presented(context.HttpContext.Request, options.HeaderName);
        if (presented is null)
        {
            return await Invoke(principal: null, context, next).ConfigureAwait(false);
        }

        var principal = await Resolve(presented, context, options).ConfigureAwait(false);

        return principal is null
            ? ProblemResultFactory.Unauthenticated(options.HeaderName)
            : await Invoke(principal, context, next).ConfigureAwait(false);
    }

    /// <summary>Resolves the presented credential, confirmed against the tenant the caller asked for.</summary>
    /// <param name="presented">The credential as the caller presented it.</param>
    /// <param name="context">The request being filtered.</param>
    /// <param name="options">The header names to read.</param>
    private ValueTask<AlvoPrincipal?> Resolve(
        string presented, EndpointFilterInvocationContext context, AlvoAuthOptions options) =>
        resolver.ResolveAsync(
            presented,
            Presented(context.HttpContext.Request, options.TenantHeaderName),
            context.HttpContext.RequestAborted);

    /// <summary>Publishes the caller for the rest of the pipeline and takes it away again.</summary>
    /// <param name="principal">The resolved caller, or <see langword="null"/> for an anonymous one.</param>
    /// <param name="context">The request being filtered.</param>
    /// <param name="next">The rest of the pipeline.</param>
    private async ValueTask<object?> Invoke(
        AlvoPrincipal? principal, EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        accessor.Principal = principal;
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        finally
        {
            accessor.Principal = null;
        }
    }

    /// <summary>
    /// The value a caller presented in <paramref name="header"/>, or <see langword="null"/> when they
    /// presented none.
    /// </summary>
    /// <remarks>
    /// An absent header and one sent empty are the same thing — no credential. Repeated headers are joined
    /// rather than resolved one at a time: an ambiguous credential must not be answered by picking whichever
    /// copy came first, and the joined text cannot be a usable key, so it lands on the 401 path. The same
    /// reading <see cref="AlvoContextFilter"/> does, for the same reasons.
    /// </remarks>
    /// <param name="request">The request to read.</param>
    /// <param name="header">The header name to read.</param>
    private static string? Presented(HttpRequest request, string header)
    {
        if (!request.Headers.TryGetValue(header, out var values))
        {
            return null;
        }

        var value = values.Count == 1 ? values[0] : string.Join(',', values.ToArray());

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
