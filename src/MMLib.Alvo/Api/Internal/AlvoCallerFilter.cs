using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The filter <c>.WithAlvoCaller()</c> adds: resolve the caller, answer 401 for a credential that cannot be used, fill the
/// request's <see cref="AlvoCaller"/> and publish the caller through <see cref="IAlvoContextAccessor"/> for the handler.
/// </summary>
/// <remarks>
/// <para>No scope check: a plain endpoint names no entity or operation, so the host's code decides what it allows.</para>
/// <para>
/// <b>Which is why a personal or service token is refused</b> (accounts S5, the final review's I1): its scopes are the
/// boundary its owner drew around a credential handed to a third party, and an endpoint that names no entity cannot keep
/// them. It is resolved first — an unusable token is any key's 401 — and then answered 403 with a fixed sentence, by the
/// same key-id test the Management API uses (<see cref="AppTokenSecret.IsAppToken"/>). Configured keys, a Bearer person and
/// the anonymous caller are admitted as before. A scope opt-in for these endpoints is #536.
/// </para>
/// </remarks>
internal sealed class AlvoCallerFilter(CallerGate gate, IAlvoContextAccessor accessor) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        using var instant = RequestInstant.Enter(context.HttpContext);

        var admission = await gate.AdmitAsync(context.HttpContext, scopeEntity: null, DataOperation.Get).ConfigureAwait(false);
        if ((admission.Refusal ?? AppTokenRefusal(admission.Principal)) is { } refused)
        {
            return refused;
        }

        AlvoCaller.For(context.HttpContext).Resolve(admission.Context);
        return await CallerResolution.PublishingAsync(accessor, admission.Principal, context, next).ConfigureAwait(false);
    }

    private static IResult? AppTokenRefusal(AlvoPrincipal? principal) =>
        AppTokenSecret.IsAppToken(principal) ? ProblemResultFactory.AppTokenCustomEndpointForbidden() : null;
}
