namespace MMLib.Alvo.Schema.Tests;

/// <summary>
/// <c>auth.signUp</c> (spec §3.1): required once <c>auth.providers</c> lists <c>google</c>, a closed object whose
/// <c>allowedEmailDomains</c> are lower-case ASCII hostnames — no wildcard, no upper case.
/// </summary>
/// <remarks>
/// The refusal of <c>google</c> without <c>signUp</c> is the negative example <c>google-without-sign-up.json</c>; the
/// facts here cover what no example declares — both keys at once, and the domain grammar.
/// </remarks>
public class SignUpSchemaTests
{
    private static string Descriptor(string signUp) =>
        $$"""
        {
          "apiVersion": "alvo.dev/v1",
          "name": "sign-up",
          "auth": { "providers": ["google"], "signUp": {{signUp}} },
          "entities": { "notes": { "fields": { "title": { "type": "string" } } } }
        }
        """;

    private static IReadOnlyList<(string Pointer, string Message)> Failures(string signUp) =>
        SchemaValidator.Failures(SchemaValidator.Load(), Descriptor(signUp));

    [Fact]
    public void Both_keys_validate() =>
        Failures("""{ "mode": "open", "allowedEmailDomains": ["kros.sk", "mail.example-co.com"] }""").ShouldBeEmpty();

    [Theory]
    [InlineData("Kros.sk")]
    [InlineData("*.kros.sk")]
    [InlineData("kros")]
    [InlineData("-kros.sk")]
    [InlineData("kros.sk.")]
    public void A_domain_that_is_not_a_lowercase_hostname_is_refused(string domain) =>
        Failures($$"""{ "mode": "open", "allowedEmailDomains": ["{{domain}}"] }""")
            .ShouldContain(failure => failure.Pointer == "#/auth/signUp/allowedEmailDomains/0");

    [Fact]
    public void An_empty_domain_list_is_refused() =>
        Failures("""{ "mode": "open", "allowedEmailDomains": [] }""")
            .ShouldContain(failure => failure.Pointer == "#/auth/signUp/allowedEmailDomains");

    [Fact]
    public void A_sign_up_without_a_mode_is_refused() =>
        Failures("""{ "allowedEmailDomains": ["kros.sk"] }""").ShouldContain(failure => failure.Pointer == "#/auth/signUp");

    [Fact]
    public void An_unknown_mode_or_key_is_refused()
    {
        Failures("""{ "mode": "closed" }""").ShouldContain(failure => failure.Pointer == "#/auth/signUp/mode");
        Failures("""{ "mode": "open", "domains": ["kros.sk"] }""").ShouldContain(failure => failure.Pointer.StartsWith("#/auth/signUp", StringComparison.Ordinal));
    }
}
