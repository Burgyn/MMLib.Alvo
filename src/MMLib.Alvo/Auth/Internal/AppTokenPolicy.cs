using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Auth.Internal;

/// <summary>
/// What the applied descriptor's <c>auth.accessTokens</c> says: which app tokens (personal, service) people may mint
/// and present, and the longest expiry a mint may ask for — compiled with the rest of the policy, so minting and
/// resolution read the descriptor that is applied (S5 plan D11, default-deny).
/// </summary>
/// <remarks>
/// Consumers read it as <c>IPolicyCatalogProvider.Current?.AppTokens ?? AppTokenPolicy.None</c>.
/// <see cref="Service"/> is on only when the descriptor also declares <c>tenancy.selfService.createTenants</c> — belt
/// and braces to <c>AccessTokensCheck</c>'s refusal, so a catalog built from a descriptor that skipped the apply still
/// never admits a service token without a shared tenant to act as.
/// </remarks>
/// <param name="Personal">Whether personal tokens are on.</param>
/// <param name="Service">Whether service tokens are on.</param>
/// <param name="MaxLifetimeDays">The longest expiry a mint may ask for, in days.</param>
internal sealed record AppTokenPolicy(bool Personal, bool Service, int MaxLifetimeDays)
{
    /// <summary>The expiry bound when the descriptor declares none (spec §3.1).</summary>
    internal const int DefaultMaxLifetimeDays = 90;

    /// <summary>The policy of no <c>auth.accessTokens</c>: no app token of either kind.</summary>
    internal static AppTokenPolicy None { get; } = new(false, false, DefaultMaxLifetimeDays);

    /// <summary>Gets whether either kind is on — the token routes answer at all only then.</summary>
    internal bool Any => Personal || Service;

    /// <summary>
    /// Whether a key of <paramref name="kind"/> may be minted or presented under this policy: a configured key always —
    /// the descriptor does not govern it — and an app token only while its kind is on.
    /// </summary>
    /// <param name="kind">The key's kind.</param>
    internal bool Allows(ApiKeyKind kind) => kind switch
    {
        ApiKeyKind.Configured => true,
        ApiKeyKind.Personal => Personal,
        ApiKeyKind.Service => Service,
        _ => false,
    };

    /// <summary>Builds the policy <paramref name="descriptor"/> declares.</summary>
    /// <param name="descriptor">The descriptor being compiled.</param>
    internal static AppTokenPolicy From(AlvoDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Auth?.AccessTokens is not { } declared)
        {
            return None;
        }

        var sharedTenants = descriptor.Tenancy?.SelfService?.CreateTenants is not null;
        return new(
            declared.Personal == true,
            declared.Service == true && sharedTenants,
            declared.MaxLifetimeDays ?? DefaultMaxLifetimeDays);
    }
}
