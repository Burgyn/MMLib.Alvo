using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Tenants;
using MMLib.Alvo.Tenants.SelfService;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// The applied descriptor's say over signing in (S2 D10): its sign-in policy, whether a provider is one it lists
/// <em>and</em> this deployment configures, and its self-service policy — which tenant a person acts in and whether a
/// sign-in owes them a personal tenant (S3 plan D5, D6).
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
    internal SignInPolicy Policy => Applied ?? SignInPolicy.None;

    /// <summary>Gets the applied descriptor's sign-in policy, or <see langword="null"/> while no descriptor is applied.</summary>
    internal SignInPolicy? Applied => catalog.Current?.SignIn;

    /// <summary>Gets the applied descriptor's self-service policy; <see cref="SelfServicePolicy.None"/> without one.</summary>
    internal SelfServicePolicy SelfService => catalog.Current?.SelfService ?? SelfServicePolicy.None;

    /// <summary>
    /// The personal tenant a sign-in or a refresh ensures under the applied descriptor (D6): a fresh id while it gives
    /// personal tenants — used only if the account has none yet — else <see langword="null"/>.
    /// </summary>
    internal PersonalTenantRequest? PersonalTenant() =>
        SelfService.HasPersonalTenant ? new PersonalTenantRequest(TenantId.New()) : null;

    /// <summary>Whether <paramref name="policy"/> lists the configured provider named <paramref name="provider"/>.</summary>
    /// <param name="policy">The sign-in policy the caller read once for this request.</param>
    /// <param name="provider">The provider's name, as an identity or a session records it.</param>
    internal bool Admits(SignInPolicy policy, string provider)
    {
        ArgumentNullException.ThrowIfNull(policy);
        return providers.TryFindByName(provider, out var configured) && policy.Lists(configured.Provider);
    }
}
