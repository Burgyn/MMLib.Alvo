using MMLib.Alvo.Rules;
using System.Diagnostics.CodeAnalysis;

namespace MMLib.Alvo.Auth;

/// <summary>
/// Gates a data operation against the scopes an API key grants, before <c>IPolicyEngine</c>
/// ever runs. Scopes are mandatory: an empty scope set denies every operation — a key without
/// scopes would otherwise be the all-powerful <c>service_role</c> anti-pattern renamed.
/// </summary>
public sealed class ScopeGate
{
    /// <summary>
    /// Answers whether any of <paramref name="principal"/>'s scopes allow <paramref name="operation"/>
    /// on <paramref name="entity"/>.
    /// </summary>
    /// <param name="principal">The resolved caller.</param>
    /// <param name="entity">The entity the operation targets.</param>
    /// <param name="operation">The operation being performed.</param>
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Registered and consumed as a DI singleton like the other auth services; kept an instance member for that parity.")]
    public bool Allows(AlvoPrincipal principal, string entity, DataOperation operation)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Scopes.Any(scope => scope.Allows(entity, operation));
    }
}
