using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Api.Internal;

/// <summary>
/// Resolves a request's credential with the same <see cref="CallerResolution"/> the generated routes use: no credential
/// is anonymous, a credential that cannot be used is 401, and — when a scope is asked for — a caller whose scopes exclude
/// it is 403.
/// </summary>
/// <param name="resolvers">The API-key resolver and, when a host registered one, the person resolver.</param>
/// <param name="scopes">Gates an operation against the caller's scopes.</param>
/// <param name="options">Carries the header names a credential and a requested tenant are read from.</param>
internal sealed class CallerGate(CallerResolvers resolvers, ScopeGate scopes, IOptions<AlvoAuthOptions> options)
{
    /// <summary>The scope entity a command bound to no entity needs: only a wildcard scope covers it.</summary>
    internal const string AnyEntity = "*";

    /// <summary>Resolves the caller of <paramref name="http"/>.</summary>
    /// <param name="http">The request.</param>
    /// <param name="scopeEntity">The entity a key's scopes must cover, or <see langword="null"/> for no scope check.</param>
    /// <param name="operation">The operation the scopes must cover.</param>
    /// <param name="alsoRead">
    /// Whether the scopes must also cover reading <paramref name="scopeEntity"/> (a command bound to its rows, Ruling 39).
    /// </param>
    internal async ValueTask<CallerAdmission> AdmitAsync(
        HttpContext http, string? scopeEntity, DataOperation operation, bool alsoRead = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        var admission = await CallerResolution.AdmitAsync(http.Request, resolvers, options.Value, CredentialSurface.Data)
            .ConfigureAwait(false);
        if (admission is not { Principal: { } principal, Refusal: null })
        {
            return admission;
        }

        return scopeEntity is null || Covers(principal, scopeEntity, operation, alsoRead)
            ? admission
            : new CallerAdmission(principal, ProblemResultFactory.ScopeRefused());
    }

    private bool Covers(AlvoPrincipal principal, string entity, DataOperation operation, bool alsoRead) =>
        scopes.Allows(principal, entity, operation) && (!alsoRead || scopes.Allows(principal, entity, DataOperation.Get));
}
