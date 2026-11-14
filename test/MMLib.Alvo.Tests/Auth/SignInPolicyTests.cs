using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The sign-in policy rides on the compiled catalog, so the exchange reads the descriptor that is applied: its name,
/// the providers it lists, and who may create an account.
/// </summary>
public sealed class SignInPolicyTests
{
    private static PolicyCatalog Compile(string auth)
    {
        var descriptor = AlvoDescriptor.Parse(auth.Length == 0
            ? """{ "apiVersion": "alvo.dev/v1", "name": "time-tracker", "entities": { "notes": { "fields": { "title": { "type": "string" } } } } }"""
            : $$"""{ "apiVersion": "alvo.dev/v1", "name": "time-tracker", "auth": {{auth}}, "entities": { "notes": { "fields": { "title": { "type": "string" } } } } }""");
        return PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler());
    }

    [Fact]
    public void The_policy_carries_the_descriptor_name_providers_mode_and_lowercased_domains()
    {
        var policy = Compile("""
            { "providers": ["local", "google"], "signUp": { "mode": "invitationOnly", "allowedEmailDomains": ["kros.sk", "example.com"] } }
            """).SignIn;

        policy.ProjectName.ShouldBe("time-tracker");
        policy.Providers.ShouldBe([AuthProvider.Local, AuthProvider.Google], ignoreOrder: true);
        policy.Mode.ShouldBe(SignUpMode.InvitationOnly);
        policy.AllowedEmailDomains.ShouldBe(["kros.sk", "example.com"]);
        policy.Lists(AuthProvider.Google).ShouldBeTrue();
        policy.Lists(AuthProvider.Microsoft).ShouldBeFalse();
    }

    /// <summary>
    /// The model is lower-cased whatever reached it: the schema refuses an upper-case domain, but a descriptor that
    /// skipped the schema pass must not turn a case difference into a sign-up refusal.
    /// </summary>
    [Fact]
    public void Domains_are_lowercased_even_when_the_schema_was_skipped()
    {
        var descriptor = AlvoDescriptor.Parse("""
            { "apiVersion": "alvo.dev/v1", "name": "p", "auth": { "providers": ["google"], "signUp": { "mode": "open", "allowedEmailDomains": ["Kros.SK"] } }, "entities": {} }
            """);

        SignInPolicy.From(descriptor).AllowedEmailDomains.ShouldBe(["kros.sk"]);
    }

    [Fact]
    public void A_descriptor_without_auth_has_no_providers_and_no_mode()
    {
        var policy = Compile(string.Empty).SignIn;

        policy.ProjectName.ShouldBe("time-tracker");
        policy.Providers.ShouldBeEmpty();
        policy.Mode.ShouldBeNull();
        policy.AllowedEmailDomains.ShouldBeEmpty();
        policy.Lists(AuthProvider.Google).ShouldBeFalse();
    }

    [Fact]
    public void No_policy_lists_no_provider()
    {
        SignInPolicy.None.Providers.ShouldBeEmpty();
        SignInPolicy.None.Mode.ShouldBeNull();
        Enum.GetValues<AuthProvider>().ShouldAllBe(provider => !SignInPolicy.None.Lists(provider));
    }
}
