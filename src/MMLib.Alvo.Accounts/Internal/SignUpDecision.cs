using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Accounts.Internal;

/// <summary>What the descriptor's <c>auth.signUp</c> says about creating an account for an identity seen for the first time.</summary>
internal enum SignUpVerdict
{
    /// <summary>A new account may be created.</summary>
    Admitted,

    /// <summary>
    /// Only an invitation creates an account, and this sign-in carries none (<c>invitationOnly</c>, S2 D14; S4 plan D5).
    /// </summary>
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
/// <b>An invitation opens <c>invitationOnly</c>, and only that</b> (S4 plan D5). A sign-in that carries an
/// <c>invitation_code</c> is judged as <c>open</c> would judge it — <c>allowedEmailDomains</c> included, because spec §3.1
/// says who "may sign up" without naming a mode — and the store then creates the account only together with the
/// invitation's membership: a dead code refuses the whole sign-in, so the code is the admission, not this verdict.
/// Without a code <c>invitationOnly</c> refuses every new identity, as it did in S2.
/// </para>
/// <list type="table">
/// <listheader><term>mode</term><description>verdict for a new identity</description></listheader>
/// <item><term><c>open</c>, with or without a code</term><description>the domain check: <see cref="SignUpVerdict.Admitted"/> or <see cref="SignUpVerdict.DomainNotAllowed"/></description></item>
/// <item><term><c>invitationOnly</c> with a code</term><description>the domain check, as <c>open</c></description></item>
/// <item><term><c>invitationOnly</c> without a code</term><description><see cref="SignUpVerdict.InvitationOnly"/></description></item>
/// <item><term>no mode</term><description><see cref="SignUpVerdict.InvitationOnly"/>, code or not</description></item>
/// </list>
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
    /// <param name="hasInvitation">Whether the sign-in carries an <c>invitation_code</c>, live or not.</param>
    internal static SignUpVerdict Decide(SignInPolicy policy, VerifiedIdentity identity, bool hasInvitation)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(identity);
        if (policy.Mode is not SignUpMode.Open && !(policy.Mode is SignUpMode.InvitationOnly && hasInvitation))
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
