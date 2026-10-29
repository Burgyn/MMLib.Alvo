using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>What resolving a request's credential decided: the caller, or the refusal to answer with.</summary>
/// <param name="Principal">The resolved key's principal, or <see langword="null"/> for an anonymous caller.</param>
/// <param name="Refusal">The 401 or 403 to answer instead, when the credential cannot be used.</param>
internal readonly record struct CallerAdmission(AlvoPrincipal? Principal, IResult? Refusal)
{
    /// <summary>Gets the caller to act as.</summary>
    internal AlvoContext Context => Principal?.Context ?? AlvoContext.Anonymous;
}

/// <summary>
/// Resolves a request's credential with the same <see cref="CallerResolution"/> the generated routes use: no credential is
/// anonymous, a credential that cannot be used is 401, and — when a scope is asked for — a key whose scopes exclude it is 403.
/// </summary>
internal sealed class CallerGate(IAlvoContextResolver resolver, ScopeGate scopes, IOptions<AlvoAuthOptions> options)
{
    /// <summary>The scope entity a command bound to no entity needs: only a wildcard scope covers it.</summary>
    internal const string AnyEntity = "*";

    /// <summary>Resolves the caller of <paramref name="http"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="scopeEntity">The entity a key's scopes must cover, or <see langword="null"/> for no scope check.</param>
    /// <param name="operation">The operation the scopes must cover.</param>
    /// <param name="alsoRead">Whether the scopes must also cover reading <paramref name="scopeEntity"/> (a command bound to its rows, Ruling 39).</param>
    internal async ValueTask<CallerAdmission> AdmitAsync(HttpContext http, string? scopeEntity, DataOperation operation, bool alsoRead = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        var settings = options.Value;
        var presented = CallerResolution.Presented(http.Request, settings.HeaderName);
        if (presented is null)
        {
            return new CallerAdmission(null, null);
        }

        var principal = await CallerResolution.ResolveAsync(resolver, presented, http.Request, settings).ConfigureAwait(false);
        if (principal is null)
        {
            return new CallerAdmission(null, ProblemResultFactory.Unauthenticated(settings.HeaderName));
        }

        return scopeEntity is null || (scopes.Allows(principal, scopeEntity, operation) && (!alsoRead || scopes.Allows(principal, scopeEntity, DataOperation.Get)))
            ? new CallerAdmission(principal, null)
            : new CallerAdmission(principal, ProblemResultFactory.ScopeRefused());
    }
}
