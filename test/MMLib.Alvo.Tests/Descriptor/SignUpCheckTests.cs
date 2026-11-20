using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// An end-user provider changes what <c>authenticated</c> means — from a provisioned caller to anyone who can sign in —
/// so <c>auth.signUp</c> must say who may create an account, and its absence is refused with both choices (spec §3.1).
/// </summary>
public sealed class SignUpCheckTests
{
    private static IReadOnlyList<DescriptorValidationError> Check(string auth) =>
        SignUpCheck.Check(AlvoDescriptor.Parse(Descriptor(auth)));

    private static string Descriptor(string auth, string tenancy = "") =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "sign-up",
          "auth": {{auth}},{{tenancy}}
          "entities": { "notes": { "fields": { "title": { "type": "string" } } } }
        }
        """;

    private const string InvitationOnly =
        """{ "providers": ["google"], "roles": ["member"], "signUp": { "mode": "invitationOnly" } }""";

    private static string SelfService(string invitations) =>
        $$"""

          "tenancy": {
            "enabled": true,
            "selfService": {
              "assignableRoles": ["member"],
              "createTenants": { "creatorRoles": ["member"] }{{invitations}}
            }
          },
        """;

    private static IReadOnlyList<DescriptorValidationError> Warnings(string auth, string tenancy = "") =>
        SignUpCheck.Warnings(AlvoDescriptor.Parse(Descriptor(auth, tenancy)));

    [Fact]
    public void Google_without_sign_up_is_refused_with_both_choices()
    {
        var error = Check("""{ "providers": ["google"] }""").ShouldHaveSingleItem();

        error.Path.ShouldBe("/auth/signUp");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldBe("auth.providers lists google, which lets people sign up, but auth.signUp says nothing about who may.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain("\"open\"");
        error.FixSuggestion.ShouldContain("\"invitationOnly\"");
    }

    [Theory]
    [InlineData("open")]
    [InlineData("invitationOnly")]
    public void Google_with_either_mode_is_accepted(string mode) =>
        Check($$"""{ "providers": ["local", "google"], "signUp": { "mode": "{{mode}}" } }""").ShouldBeEmpty();

    [Fact]
    public void Local_alone_needs_no_sign_up() =>
        Check("""{ "providers": ["local"] }""").ShouldBeEmpty();

    [Fact]
    public void Sign_up_without_an_end_user_provider_is_accepted() =>
        Check("""{ "providers": ["local"], "signUp": { "mode": "open" } }""").ShouldBeEmpty();

    /// <summary>
    /// <b>The apply path carries the same refusal</b>: the schema refuses the descriptor first, and the validator
    /// reports its <c>required</c> failure as this check's finding rather than as a bare keyword, so an author still
    /// reads both choices. The schema's <c>allOf</c>/<c>then</c> wrappers stay, as they do for every conditional.
    /// </summary>
    [Fact]
    public void The_validator_reports_the_refusal_with_both_choices()
    {
        var result = new DescriptorValidator().Validate(Descriptor("""{ "providers": ["google"] }"""));

        result.IsValid.ShouldBeFalse();
        result.Errors.Where(error => error == SignUpCheck.Refusal).ShouldHaveSingleItem("the schema pass and the rule pass must not both report it");
        result.Errors.ShouldNotContain(error => error.Message.Contains("required property 'signUp'", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>invitationOnly</c> where nobody can ever be invited (stack review C-I6): no tenancy, or self-service without
    /// invitations. A legitimate "registration closed" state — existing accounts still sign in — so a warning with both
    /// ways out, never a refusal.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("selfService-without-invitations")]
    [InlineData("selfService-with-invitations-off")]
    public void Invitation_only_without_invitations_is_warned_with_both_fixes(string tenancy)
    {
        var block = tenancy switch
        {
            "selfService-without-invitations" => SelfService(""),
            "selfService-with-invitations-off" => SelfService(",\r\n      \"invitations\": false"),
            _ => "",
        };

        var warning = Warnings(InvitationOnly, block).ShouldHaveSingleItem();

        warning.ShouldBe(SignUpCheck.NobodyCanBeInvited);
        warning.Path.ShouldBe("/auth/signUp/mode");
        warning.Severity.ShouldBe(DescriptorValidationSeverity.Warning);
        warning.FixSuggestion.ShouldNotBeNull().ShouldContain("\"invitations\": true");
        warning.FixSuggestion.ShouldContain("\"open\"");
    }

    [Fact]
    public void Invitation_only_with_invitations_is_not_warned() =>
        Warnings(InvitationOnly, SelfService(",\r\n      \"invitations\": true")).ShouldBeEmpty();

    [Fact]
    public void Open_sign_up_and_invitation_only_without_an_end_user_provider_are_not_warned()
    {
        Warnings("""{ "providers": ["google"], "signUp": { "mode": "open" } }""").ShouldBeEmpty();
        Warnings("""{ "providers": ["local"], "signUp": { "mode": "invitationOnly" } }""").ShouldBeEmpty();
    }

    [Fact]
    public void The_validator_carries_the_warning_and_still_applies()
    {
        var result = new DescriptorValidator().Validate(Descriptor(InvitationOnly));

        result.Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error).ShouldBeEmpty();
        result.Errors.ShouldContain(SignUpCheck.NobodyCanBeInvited);
    }

    [Fact]
    public void The_validator_accepts_google_with_a_sign_up() =>
        new DescriptorValidator().Validate(Descriptor("""{ "providers": ["google"], "signUp": { "mode": "open" } }"""))
            .Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error).ShouldBeEmpty();
}
