using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The applied descriptor's say over signing in (S2 D10): its sign-in policy, and whether a provider is one it lists
/// <em>and</em> this deployment configures.
/// </summary>
/// <remarks>
/// Read per call from the policy catalog, so a descriptor that drops a provider closes it on the apply that drops it — for
/// the exchange, the refresh and (Task 9) every Bearer request alike.
/// </remarks>
/// <param name="catalog">The applied policy catalog.</param>
/// <param name="providers">The providers this deployment configured.</param>
internal sealed class DescriptorSignIn(IPolicyCatalogProvider catalog, OidcProviders providers)
{
    /// <summary>Gets the applied descriptor's sign-in policy; <see cref="SignInPolicy.None"/> before any apply.</summary>
    internal SignInPolicy Policy => catalog.Current?.SignIn ?? SignInPolicy.None;

    /// <summary>Whether <paramref name="policy"/> lists the configured provider named <paramref name="provider"/>.</summary>
    /// <param name="policy">The sign-in policy the caller read once for this request.</param>
    /// <param name="provider">The provider's name, as an identity or a session records it.</param>
    internal bool Admits(SignInPolicy policy, string provider)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return providers.TryFindByName(provider, out var configured) && policy.Lists(configured.Provider);
    }
}
