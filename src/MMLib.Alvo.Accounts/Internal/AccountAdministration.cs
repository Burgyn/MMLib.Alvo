namespace MMLib.Alvo.Accounts.Internal;

/// <summary>
/// <see cref="IAlvoAccountAdministration"/> over the account store, on the clock every lifetime is judged on.
/// </summary>
/// <remarks>
/// A thin adapter on purpose: disabling and revoking every session is one store call so that one transaction covers it
/// (spec §5.6, S2 D2), and the instant is read here because the store reads no clock.
/// </remarks>
/// <param name="store">The account store.</param>
/// <param name="clock">The clock the change is stamped with.</param>
internal sealed class AccountAdministration(IAccountStore store, TimeProvider clock) : IAlvoAccountAdministration
{
    /// <inheritdoc/>
    public Task<bool> SetDisabledAsync(UserId account, bool disabled, CancellationToken ct)
        => store.SetDisabledAsync(account, disabled, clock.GetUtcNow(), ct);
}
