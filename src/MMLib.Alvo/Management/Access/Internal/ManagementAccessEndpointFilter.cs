using Microsoft.AspNetCore.Http;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Management.Internal;

/// <summary>
/// Runs the management gate before the handler, and answers <c>403</c> when the caller matches no level.
/// </summary>
/// <remarks>
/// <para>
/// <b>Before the handler, never after.</b> A gate that filters a handler's output has already let the
/// handler read what it was refused; this returns instead of calling <c>next</c>, which is the same rule
/// the security-core checklist states for a data query — the predicate goes in the WHERE, not in a
/// post-filter.
/// </para>
/// <para>
/// <b>No principal means the anonymous caller, not a 401.</b> The Data API's own filter settles that
/// distinction: a caller with no credential is one whose policy happens to permit nothing, and
/// <see cref="AlvoContext.Anonymous"/> resolves to <see cref="ManagementLevel.None"/> through the very
/// same default-deny path a credentialled caller who matches nothing takes.
/// </para>
/// </remarks>
/// <param name="operation">The operation the route this filter is attached to performs.</param>
/// <param name="access">The gate that resolves the caller's level.</param>
/// <param name="callers">Where the caller resolved for this request is published.</param>
internal sealed class ManagementAccessEndpointFilter(
    ManagementOperation operation,
    ManagementAccessEvaluator access,
    IAlvoContextAccessor callers) : IEndpointFilter
{
    /// <inheritdoc/>
    public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var caller = callers.Principal?.Context ?? AlvoContext.Anonymous;
        return access.Allows(operation, caller)
            ? next(context)
            : ValueTask.FromResult<object?>(ProblemResultFactory.ManagementForbidden());
    }
}
