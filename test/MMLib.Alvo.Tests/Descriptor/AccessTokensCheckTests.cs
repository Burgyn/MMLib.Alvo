using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// <c>auth.accessTokens</c>'s apply refusal (spec §3.1, S5 plan D8): a service token acts as a shared tenant, so it needs
/// <c>tenancy.selfService.createTenants</c>; a personal token needs nothing else.
/// </summary>
public sealed class AccessTokensCheckTests
{
    private const string LevelThreeTenancy = """
        {
          "enabled": true,
          "selfService": {
            "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 5 },
            "invitations": true,
            "assignableRoles": ["dispatcher", "technician"]
          }
        }
        """;

    private static AlvoDescriptor Descriptor(string accessTokens, string? tenancy) =>
        AlvoDescriptor.Parse(Json(accessTokens, tenancy));

    private static string Json(string accessTokens, string? tenancy) =>
        $$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tokens",
              "auth": {
                "providers": ["google"],
                "roles": ["dispatcher", "technician"],
                "signUp": { "mode": "open" },
                "accessTokens": {{accessTokens}}
              },
              {{(tenancy is null ? string.Empty : $"\"tenancy\": {tenancy},")}}
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """;

    [Fact]
    public void Service_tokens_without_self_service_are_refused()
    {
        var error = AccessTokensCheck.Check(Descriptor("""{ "service": true }""", """{ "enabled": true }""")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/auth/accessTokens/service");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldBe(
            "Service tokens act as a shared tenant, and without tenancy.selfService no tenant has an owner to mint one.");
        error.FixSuggestion.ShouldBe(
            "Add \"tenancy\": { \"enabled\": true, \"selfService\": { \"createTenants\": { … }, \"assignableRoles\": [ … ] } }, or set service to false.");
    }

    [Fact]
    public void Service_tokens_without_create_tenants_are_refused()
    {
        var error = AccessTokensCheck.Check(Descriptor(
            """{ "personal": true, "service": true }""",
            """{ "enabled": true, "selfService": { "personalTenant": { "roles": [] } } }""")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/auth/accessTokens/service");
        error.Message.ShouldBe("Service tokens act as a shared tenant, and without createTenants no shared tenant can exist.");
        error.FixSuggestion.ShouldBe("Add \"createTenants\": { \"creatorRoles\": [ … ] } to tenancy.selfService, or set service to false.");
    }

    [Theory]
    [InlineData("""{ "service": false }""")]
    [InlineData("""{}""")]
    public void Service_tokens_declined_need_nothing(string accessTokens) =>
        AccessTokensCheck.Check(Descriptor(accessTokens, tenancy: null)).ShouldBeEmpty();

    [Fact]
    public void Personal_tokens_need_nothing_else() =>
        AccessTokensCheck.Check(Descriptor("""{ "personal": true, "maxLifetimeDays": 30 }""", tenancy: null)).ShouldBeEmpty();

    [Fact]
    public void The_spec_level_three_descriptor_is_accepted()
    {
        const string accessTokens = """{ "personal": true, "service": true }""";

        AccessTokensCheck.Check(Descriptor(accessTokens, LevelThreeTenancy)).ShouldBeEmpty();
        new DescriptorValidator().Validate(Json(accessTokens, LevelThreeTenancy)).Errors
            .ShouldNotContain(error => error.Path.StartsWith("/auth/accessTokens", StringComparison.Ordinal));
    }

    [Fact]
    public void The_validator_reports_the_refusal_once() =>
        new DescriptorValidator().Validate(Json("""{ "service": true }""", tenancy: null)).Errors
            .Count(error => error.Path == "/auth/accessTokens/service").ShouldBe(1);
}
