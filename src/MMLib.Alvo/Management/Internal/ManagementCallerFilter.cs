using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

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
/// <b>The credential mechanics are <see cref="CallerResolution"/>'s, not a second copy.</b> Reading the
/// header, resolving it and publishing the caller are exactly what <see cref="AlvoContextFilter"/> does, and
/// the header reading carries a security rule (an ambiguous credential is refused, never disambiguated) that
/// must have one home.
/// </para>
/// <para>
/// <b>The scope gate is deliberately not applied here</b>, unlike on a Data API route — the F5 design's D7.
/// An <see cref="ApiKeyScope"/> is <c>&lt;entity|*&gt;:&lt;read|write&gt;</c> and the management surface is
/// not an entity, so there is no scope for it to narrow; what a caller may do to a project's configuration
/// is the descriptor's <c>access</c> block, evaluated behind the gate.
/// </para>
/// <para>
/// <b>Three outcomes, the same three the Data API's filter has.</b> No credential at all is anonymous rather
/// than 401 — the gate refuses an anonymous caller through the same default-deny path a credentialled caller
/// who matches no level takes. A credential that was presented and cannot be used is 401, before the gate is
/// consulted: the caller believes they hold a credential and do not, which is a different fix from an access
/// block that does not name them.
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
        var request = context.HttpContext.Request;
        var presented = CallerResolution.Presented(request, options.HeaderName);
        if (presented is null)
        {
            return await CallerResolution.PublishingAsync(accessor, null, context, next).ConfigureAwait(false);
        }

        var principal = await CallerResolution.ResolveAsync(resolver, presented, request, options)
            .ConfigureAwait(false);

        return principal is null
            ? ProblemResultFactory.Unauthenticated(options.HeaderName)
            : await CallerResolution.PublishingAsync(accessor, principal, context, next).ConfigureAwait(false);
    }
}
