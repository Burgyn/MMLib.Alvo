using MMLib.Alvo.Accounts.Internal;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Accounts.Tests;

/// <summary>The sign-up policy (spec §3.1, §5.3; S2 D14) as a pure table: who may become a new account.</summary>
public sealed class SignUpDecisionTests
{
    [Theory]
    [InlineData("open", "", "ada@gmail.com", true, nameof(SignUpVerdict.Admitted))]
    [InlineData("open", "", null, false, nameof(SignUpVerdict.Admitted))]
    [InlineData("invitationOnly", "", "ada@gmail.com", true, nameof(SignUpVerdict.InvitationOnly))]
    [InlineData("invitationOnly", "kros.sk", "ada@kros.sk", true, nameof(SignUpVerdict.InvitationOnly))]
    [InlineData("none", "", "ada@gmail.com", true, nameof(SignUpVerdict.InvitationOnly))]
    [InlineData("open", "kros.sk", "ada@gmail.com", true, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", "ada@kros.sk", true, nameof(SignUpVerdict.Admitted))]
    [InlineData("open", "kros.sk", "ada@KROS.SK", true, nameof(SignUpVerdict.Admitted))]
    [InlineData("open", "kros.sk", "ada@sub.kros.sk", true, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", "ada@kros.sk.evil.test", true, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", "ada@kros.sk", false, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", null, true, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", "kros.sk", true, nameof(SignUpVerdict.DomainNotAllowed))]
    [InlineData("open", "kros.sk", "a@b@kros.sk", true, nameof(SignUpVerdict.Admitted))]
    [InlineData("open", "kros.sk,example.org", "bob@example.org", true, nameof(SignUpVerdict.Admitted))]
    public void The_policy_decides_who_may_become_a_new_account(
        string mode, string domains, string? email, bool trusted, string expected)
    {
        var policy = new SignInPolicy(
            "time-tracker",
            new HashSet<AuthProvider> { AuthProvider.Google },
            mode switch { "open" => SignUpMode.Open, "invitationOnly" => SignUpMode.InvitationOnly, _ => null },
            domains.Length == 0 ? [] : domains.Split(','));
        var identity = new VerifiedIdentity("google", "sub-1", email, trusted, "Ada", null);

        SignUpDecision.Decide(policy, identity).ShouldBe(Enum.Parse<SignUpVerdict>(expected));
    }
}
