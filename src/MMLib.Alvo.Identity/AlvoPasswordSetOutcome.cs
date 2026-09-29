namespace MMLib.Alvo.Identity;

/// <summary>What <see cref="AlvoSignIn.SetPasswordAsync"/> did with a credential token.</summary>
/// <remarks>
/// <para>
/// <b>Three members and no more.</b> A host must tell a weak password, which is safe to say and which the
/// person can fix, from a refusal, which must say nothing — so a <see langword="bool"/> would force a
/// second call and move the policy-before-token order into every host.
/// </para>
/// <para>
/// <b><see cref="Refused"/> deliberately merges every other failure</b>: an address with no account, a
/// disabled account, the bootstrap administrator, and a token that has expired, was used already, belongs
/// to somebody else or was copied incompletely. A screen that told them apart would tell a stranger which
/// addresses have accounts.
/// </para>
/// </remarks>
public enum AlvoPasswordSetOutcome
{
    /// <summary>The password is stored, and the token and every session the person held are ended.</summary>
    Set,

    /// <summary>Nothing was written, for a reason that is deliberately not said.</summary>
    Refused,

    /// <summary>
    /// The password policy refused the password; nothing about the account or the token was read, and the
    /// token still works.
    /// </summary>
    PasswordRejected,
}
