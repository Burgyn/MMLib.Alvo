using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// The filter <c>.WithAlvoCaller()</c> adds: resolve the caller, answer 401 for a credential that cannot be used, fill the
/// request's <see cref="AlvoCaller"/> and publish the caller through <see cref="IAlvoContextAccessor"/> for the handler.
/// </summary>
/// <remarks>No scope check: a plain endpoint names no entity or operation, so the host's code decides what it allows.</remarks>
internal sealed class AlvoCallerFilter(CallerGate gate, IAlvoContextAccessor accessor) : IEndpointFilter
{
    /// <inheritdoc/>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        using var instant = RequestInstant.Enter(context.HttpContext);

        var admission = await gate.AdmitAsync(context.HttpContext, scopeEntity: null, DataOperation.Get).ConfigureAwait(false);
        if (admission.Refusal is { } refused)
        {
            return refused;
        }

        AlvoCaller.For(context.HttpContext).Resolve(admission.Context);
        return await CallerResolution.PublishingAsync(accessor, admission.Principal, context, next).ConfigureAwait(false);
    }
}
