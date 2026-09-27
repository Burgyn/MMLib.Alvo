namespace MMLib.Alvo.Admin.Internal;

/// <summary>
/// The password policy as the set-password page states it: the box's bounds and the sentences that say them.
/// </summary>
/// <remarks>
/// <b>A copy of the identity package's numbers, pinned rather than referenced.</b> The policy is
/// <c>MMLib.Alvo.Identity</c>'s (NIST SP 800-63B-4, 15 to 128 characters, no composition rules, not the address),
/// and this package holds no reference to it (design §1.2). So the numbers are spelled here, for the box's
/// <c>minlength</c>/<c>maxlength</c> and the sentence under it, and a host test asserts they equal what the host
/// registered — the <c>TenantClaimType</c> precedent. The endpoint's own check is the package's, so a drift here
/// would mislead the sentence, never admit a password.
/// </remarks>
internal static class SetPasswordPolicy
{
    /// <summary>The fewest characters a password may have.</summary>
    public const int MinimumLength = 15;

    /// <summary>The most characters a password may have.</summary>
    public const int MaximumLength = 128;

    /// <summary>The policy, stated under the new password's box.</summary>
    public const string Hint
        = "At least 15 characters. Spaces and any other characters are fine, and a long phrase is best. "
            + "It cannot contain your email address.";

    /// <summary>What the page says when the policy refused the password.</summary>
    public const string Weak = "Choose a password of at least 15 characters that is not your email address.";
}
