using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What the descriptor's <c>auth.signUp</c> says about creating an account for an identity seen for the first time.</summary>
internal enum SignUpVerdict
{
    /// <summary>A new account may be created.</summary>
    Admitted,

    /// <summary>Only an invitation creates an account (<c>invitationOnly</c>, S2 D14).</summary>
    InvitationOnly,

    /// <summary>The identity's email is not a trusted address in one of <c>allowedEmailDomains</c>.</summary>
    DomainNotAllowed,
}

/// <summary>
/// The sign-up policy of spec §3.1 and §5.2 step 4, decided before the store is asked: whether a <em>new</em> identity may
/// become an account. An identity that already has one signs in whatever this says — the store reads the verdict only
/// when it would create.
/// </summary>
/// <remarks>
/// <para>
/// <b>A domain is the text after the email's last <c>@</c>, compared exactly and ignoring case</b> — no subdomain counts as
/// its parent, because <c>allowedEmailDomains</c> names hostnames, not suffixes. Only a <b>trusted</b> email (spec §5.3)
/// can satisfy it: an address a provider lets a person type unverified proves nothing about where they work.
/// </para>
/// <para>
/// <b>No declared mode admits nobody.</b> Apply refuses a descriptor that lists an end-user provider without
/// <c>auth.signUp</c>, so a policy without a mode never reaches a sign-in; if one did, default-deny is the answer.
/// </para>
/// </remarks>
internal static class SignUpDecision
{
    /// <summary>The verdict <paramref name="policy"/> gives <paramref name="identity"/>.</summary>
    /// <param name="policy">The applied descriptor's sign-in policy.</param>
    /// <param name="identity">The verified identity.</param>
    internal static SignUpVerdict Decide(SignInPolicy policy, VerifiedIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(identity);
        if (policy.Mode is not SignUpMode.Open)
        {
            return SignUpVerdict.InvitationOnly;
        }

        return policy.AllowedEmailDomains.Count == 0 || IsInAllowedDomain(policy, identity)
            ? SignUpVerdict.Admitted
            : SignUpVerdict.DomainNotAllowed;
    }

    private static bool IsInAllowedDomain(SignInPolicy policy, VerifiedIdentity identity)
    {
        var at = identity.Email?.LastIndexOf('@') ?? -1;
        if (!identity.EmailTrusted || at < 0)
        {
            return false;
        }

        var domain = identity.Email![(at + 1)..];
        return policy.AllowedEmailDomains.Any(allowed => string.Equals(allowed, domain, StringComparison.OrdinalIgnoreCase));
    }
}
