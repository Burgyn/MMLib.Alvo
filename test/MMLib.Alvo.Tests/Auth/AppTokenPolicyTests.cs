using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The app-token policy rides on the compiled catalog (S5 plan D11): absent <c>auth.accessTokens</c> is no token of
/// either kind, the lifetime bound defaults to 90 days, and a service token needs a shared tenant to act as.
/// </summary>
public sealed class AppTokenPolicyTests
{
    private const string SharedTenants = """
        { "enabled": true, "selfService": { "createTenants": { "creatorRoles": ["dispatcher"] }, "assignableRoles": ["dispatcher"] } }
        """;

    private static AlvoDescriptor Descriptor(string? accessTokens, string? tenancy = null) =>
        AlvoDescriptor.Parse($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tokens",
              "auth": {
                "providers": ["google"],
                "roles": ["dispatcher"],
                "signUp": { "mode": "open" }
                {{(accessTokens is null ? string.Empty : $", \"accessTokens\": {accessTokens}")}}
              },
              {{(tenancy is null ? string.Empty : $"\"tenancy\": {tenancy},")}}
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);

    private static AppTokenPolicy Compile(AlvoDescriptor descriptor) =>
        PolicyCatalog.Build(descriptor, DescriptorToSchemaMapper.Map(descriptor), new CelCompiler()).AppTokens;

    [Fact]
    public void Absent_is_none()
    {
        var policy = Compile(Descriptor(accessTokens: null));

        policy.ShouldBe(AppTokenPolicy.None);
        policy.Personal.ShouldBeFalse();
        policy.Service.ShouldBeFalse();
        policy.Any.ShouldBeFalse();
        policy.MaxLifetimeDays.ShouldBe(AppTokenPolicy.DefaultMaxLifetimeDays);
    }

    [Fact]
    public void Max_lifetime_defaults_to_ninety()
    {
        var policy = Compile(Descriptor("""{ "personal": true }"""));

        policy.MaxLifetimeDays.ShouldBe(90);
        policy.Personal.ShouldBeTrue();
        policy.Any.ShouldBeTrue();
    }

    [Fact]
    public void A_declared_max_lifetime_carries_through() =>
        Compile(Descriptor("""{ "personal": true, "maxLifetimeDays": 365 }""")).MaxLifetimeDays.ShouldBe(365);

    /// <summary>Built from a descriptor <c>AccessTokensCheck</c> refuses: the policy still never admits the kind.</summary>
    [Fact]
    public void Service_is_off_without_create_tenants_even_if_declared()
    {
        AppTokenPolicy.From(Descriptor("""{ "service": true }""")).Service.ShouldBeFalse();
        AppTokenPolicy.From(Descriptor(
            """{ "service": true }""",
            """{ "enabled": true, "selfService": { "personalTenant": { "roles": [] } } }""")).Any.ShouldBeFalse();
    }

    [Fact]
    public void Both_kinds_carry_through()
    {
        var policy = Compile(Descriptor("""{ "personal": true, "service": true, "maxLifetimeDays": 30 }""", SharedTenants));

        policy.ShouldBe(new AppTokenPolicy(Personal: true, Service: true, MaxLifetimeDays: 30));
    }

    [Fact]
    public void Declined_kinds_are_off() =>
        Compile(Descriptor("""{ "personal": false, "service": false }""", SharedTenants)).Any.ShouldBeFalse();

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Allows_configured_always_and_each_kind_only_when_on(bool personal, bool service)
    {
        var policy = new AppTokenPolicy(personal, service, AppTokenPolicy.DefaultMaxLifetimeDays);

        policy.Allows(ApiKeyKind.Configured).ShouldBeTrue();
        policy.Allows(ApiKeyKind.Personal).ShouldBe(personal);
        policy.Allows(ApiKeyKind.Service).ShouldBe(service);
        policy.Allows((ApiKeyKind)42).ShouldBeFalse();
    }
}
