using MMLib.Alvo.Tenants;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// <see cref="IAlvoAccountAdministration"/> over the account and tenant stores, on the clock every lifetime is judged on.
/// </summary>
/// <remarks>
/// A thin adapter on purpose: disabling and revoking every session is one store call so that one transaction covers it
/// (spec §5.6, S2 D2), and so is granting ownership (#543); the instant is read here because the stores read no clock. The
/// tenants a ban orphaned are read right after it commits: the answer is advice to the operator, and a grant racing the
/// ban shows on the next call, which is idempotent.
/// </remarks>
/// <param name="accounts">The account store.</param>
/// <param name="tenants">The tenant registry.</param>
/// <param name="clock">The clock the change is stamped with.</param>
internal sealed class AccountAdministration(IAccountStore accounts, ITenantStore tenants, TimeProvider clock) : IAlvoAccountAdministration
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<OrphanedTenant>?> SetDisabledAsync(UserId account, bool disabled, CancellationToken ct)
    {
        if (!await accounts.SetDisabledAsync(account, disabled, clock.GetUtcNow(), ct).ConfigureAwait(false))
        {
            return null;
        }

        return await tenants.ListOrphanedTenantsAsync(account, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<OwnershipGrantOutcome> GrantOwnershipAsync(TenantId tenant, UserId account, IReadOnlyList<string>? roles, CancellationToken ct)
        => tenants.GrantOwnershipAsync(new OwnershipGrant(tenant, account, roles, clock.GetUtcNow()), ct);
}
