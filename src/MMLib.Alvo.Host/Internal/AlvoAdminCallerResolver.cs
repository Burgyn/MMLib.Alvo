using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Admin;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity;
using System.Security.Claims;

namespace MMLib.Alvo.Host.Internal;

/// <summary>
/// Fills <see cref="IAlvoAdminCallerResolver"/> from the identity package's cookie resolver.
/// </summary>
/// <remarks>
/// <para>
/// <b>It resolves through the keyed resolver, and the key is the point.</b>
/// <c>AlvoIdentity.ResolverKey</c> names the resolver whose "presented key" is a subject ASP.NET
/// Core already authenticated. The <em>unkeyed</em> resolver is the one the Data API hands a raw
/// API-key header to — so resolving through that one would make a user's uuid a working API key,
/// which is the exact hole <c>AddAlvoIdentity</c>'s own remarks say the keyed registration exists
/// to prevent.
/// </para>
/// <para>
/// No tenant is requested. The operator holds one or holds none, and the resolver answers with it;
/// asking for a particular one would be asking a question the dashboard has no way to have an
/// opinion about (§2.7).
/// </para>
/// </remarks>
/// <param name="resolvers">The keyed resolver, resolved per call because it is scoped.</param>
internal sealed class AlvoAdminCallerResolver(IServiceProvider resolvers) : IAlvoAdminCallerResolver
{
    /// <inheritdoc/>
    public ValueTask<AlvoPrincipal?> ResolveAsync(
        ClaimsPrincipal signedIn, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(signedIn);

        var subject = signedIn.FindFirstValue(ClaimTypes.NameIdentifier);
        if (subject is not { Length: > 0 })
        {
            return ValueTask.FromResult<AlvoPrincipal?>(null);
        }

        var resolver = resolvers.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey);
        return resolver.ResolveAsync(subject, requestedTenant: null, cancellationToken);
    }
}
