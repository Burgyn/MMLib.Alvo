using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// <c>auth.apiTokens</c>'s apply refusal (spec §3.1, S5 plan D8): a service token acts as a shared tenant, so it needs
/// <c>tenancy.selfService.createTenants</c>; a personal token needs nothing else.
/// </summary>
public sealed class ApiTokensCheckTests
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

    private static AlvoDescriptor Descriptor(string apiTokens, string? tenancy) =>
        AlvoDescriptor.Parse(Json(apiTokens, tenancy));

    private static string Json(string apiTokens, string? tenancy) =>
        $$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "tokens",
              "auth": {
                "providers": ["google"],
                "roles": ["dispatcher", "technician"],
                "signUp": { "mode": "open" },
                "apiTokens": {{apiTokens}}
              },
              {{(tenancy is null ? string.Empty : $"\"tenancy\": {tenancy},")}}
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """;

    [Fact]
    public void Service_tokens_without_self_service_are_refused()
    {
        var error = ApiTokensCheck.Check(Descriptor("""{ "service": true }""", """{ "enabled": true }""")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/auth/apiTokens/service");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldBe(
            "Service tokens act as a shared tenant, and without tenancy.selfService no tenant has an owner to mint one.");
        error.FixSuggestion.ShouldBe(
            "Add \"tenancy\": { \"enabled\": true, \"selfService\": { \"createTenants\": { … }, \"assignableRoles\": [ … ] } }, or set service to false.");
    }

    [Fact]
    public void Service_tokens_without_create_tenants_are_refused()
    {
        var error = ApiTokensCheck.Check(Descriptor(
            """{ "personal": true, "service": true }""",
            """{ "enabled": true, "selfService": { "personalTenant": { "roles": [] } } }""")).ShouldHaveSingleItem();

        error.Path.ShouldBe("/auth/apiTokens/service");
        error.Message.ShouldBe("Service tokens act as a shared tenant, and without createTenants no shared tenant can exist.");
        error.FixSuggestion.ShouldBe("Add \"createTenants\": { \"creatorRoles\": [ … ] } to tenancy.selfService, or set service to false.");
    }

    [Theory]
    [InlineData("""{ "service": false }""")]
    [InlineData("""{}""")]
    public void Service_tokens_declined_need_nothing(string apiTokens) =>
        ApiTokensCheck.Check(Descriptor(apiTokens, tenancy: null)).ShouldBeEmpty();

    [Fact]
    public void Personal_tokens_need_nothing_else() =>
        ApiTokensCheck.Check(Descriptor("""{ "personal": true, "maxLifetimeDays": 30 }""", tenancy: null)).ShouldBeEmpty();

    [Fact]
    public void The_spec_level_three_descriptor_is_accepted()
    {
        const string apiTokens = """{ "personal": true, "service": true }""";

        ApiTokensCheck.Check(Descriptor(apiTokens, LevelThreeTenancy)).ShouldBeEmpty();
        new DescriptorValidator().Validate(Json(apiTokens, LevelThreeTenancy)).Errors
            .ShouldNotContain(error => error.Path.StartsWith("/auth/apiTokens", StringComparison.Ordinal));
    }

    [Fact]
    public void The_validator_reports_the_refusal_once() =>
        new DescriptorValidator().Validate(Json("""{ "service": true }""", tenancy: null)).Errors
            .Count(error => error.Path == "/auth/apiTokens/service").ShouldBe(1);
}
