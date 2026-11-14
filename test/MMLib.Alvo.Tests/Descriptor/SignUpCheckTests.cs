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

    private static string Descriptor(string auth) =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "sign-up",
          "auth": {{auth}},
          "entities": { "notes": { "fields": { "title": { "type": "string" } } } }
        }
        """;

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

    [Fact]
    public void The_validator_accepts_google_with_a_sign_up() =>
        new DescriptorValidator().Validate(Descriptor("""{ "providers": ["google"], "signUp": { "mode": "open" } }"""))
            .Errors.Where(error => error.Severity == DescriptorValidationSeverity.Error).ShouldBeEmpty();
}
