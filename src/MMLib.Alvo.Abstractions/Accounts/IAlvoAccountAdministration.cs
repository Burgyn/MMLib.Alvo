namespace MMLib.Alvo.Accounts;

/// <summary>
/// What an operator does to an end user's account (spec §5.6): disable it — which ends every session it has — or enable it
/// again.
/// </summary>
/// <remarks>
/// <para>
/// <b>Internal, and its only caller is the gated management route</b>
/// (<c>PUT {m}/projects/{project}/accounts/{account}/disabled</c>, operation <c>ManageAccounts</c> at <c>admin</c>). The
/// route's gate is therefore the whole of the authorization, which holds only while nothing else can reach this port.
/// When something else must — a dashboard view of end users (spec §10) — it becomes public in the
/// <c>IAlvoUserAdministration</c> shape: the implementation registered under an unguarded key and a core decorator
/// (<c>GuardedUserAdministration</c>'s precedent) the only registration under the plain interface, so an in-process
/// caller meets the same gate an HTTP one does.
/// </para>
/// <para>
/// <b>Not a member of <c>IAlvoUserAdministration</c>.</b> That port administers the people who manage a project, through
/// the identity package; an end user signs in through <c>MMLib.Alvo.Accounts</c>, into a different table, and a
/// deployment can have either without the other.
/// </para>
/// </remarks>
internal interface IAlvoAccountAdministration
{
    /// <summary>
    /// Disables <paramref name="account"/> and revokes every session it has, in one transaction — so its next request and
    /// its next refresh are refused — or enables it again, which revokes every session it still has too: the person signs
    /// in afresh, and no session from before the enable comes back.
    /// </summary>
    /// <param name="account">The account's id.</param>
    /// <param name="disabled">Whether the account is to be disabled.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns><see langword="false"/> when there is no such account.</returns>
    Task<bool> SetDisabledAsync(UserId account, bool disabled, CancellationToken ct);
}
