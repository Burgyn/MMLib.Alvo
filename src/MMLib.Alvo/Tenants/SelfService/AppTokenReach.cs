using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Api.Internal;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;

namespace MMLib.Alvo.Tenants.SelfService;

/// <summary>
/// Whether a token minted here would ever resolve — asked before <c>POST {prefix}/tokens</c> mints one, so no host shows a
/// credential nothing will accept (S5 Task 9 fix round 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The question goes to the key store the resolver really uses</b>: the <see cref="IApiKeyStore"/> the container
/// resolves. Alvo's routing store (<see cref="ApiKeyStores"/>) answers <see cref="ApiKeyStores.ReachProbeKeyId"/> with
/// <see cref="ApiKeyStores.ReachProbe"/> while it has a token registry. A host that <em>decorates</em> it passes that
/// record through, and keeps minting; a host that <em>replaced</em> it — or a host with no registry — answers anything
/// else, and the route refuses with the <c>feature-disabled</c> 404 naming <c>auth.accessTokens</c> and the cause.
/// </para>
/// <para>
/// <b>Not a boot refusal:</b> the descriptor is not known at boot, and a host that replaces the store and never turns
/// <c>auth.accessTokens</c> on is legitimate. One probe per mint, answered in memory by Alvo's store.
/// </para>
/// </remarks>
internal static class AppTokenReach
{
    private const string Unreachable =
        "This host does not resolve personal or service tokens: its API-key store (IApiKeyStore) was replaced by one that "
        + "never asks Alvo's token registry, or no registry is registered. A token minted here could never be used. "
        + "Decorate Alvo's IApiKeyStore instead of replacing it, or remove auth.accessTokens from the descriptor.";

    /// <summary>The refusal when a token minted on this host could never resolve; otherwise <see langword="null"/>.</summary>
    /// <param name="http">The request, whose services hold the resolved key store.</param>
    /// <param name="ct">A token to cancel the probe.</param>
    internal static async ValueTask<IResult?> RefusalAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var store = http.RequestServices.GetRequiredService<IApiKeyStore>();
        var answer = await store.FindAsync(ApiKeyStores.ReachProbeKeyId, ct).ConfigureAwait(false);
        return Equals(answer, ApiKeyStores.ReachProbe)
            ? null
            : ProblemResultFactory.FeatureDisabled(AppTokenProblems.AccessTokensKey, Unreachable);
    }
}
