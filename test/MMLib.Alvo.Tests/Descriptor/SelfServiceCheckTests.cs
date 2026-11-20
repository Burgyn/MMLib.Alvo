using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Descriptor;

/// <summary>
/// <c>tenancy.selfService</c>'s apply refusals (spec §3.1): self-service tenants are tenants, invitations need a shared
/// tenant to invite into, and every membership role is a tenant role — an application role from <c>auth.roles</c>,
/// never a built-in one.
/// </summary>
public sealed class SelfServiceCheckTests
{
    private static IReadOnlyList<DescriptorValidationError> Check(string tenancy, string roles = """["dispatcher", "technician"]""") =>
        SelfServiceCheck.Check(AlvoDescriptor.Parse($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "self-service",
              "auth": { "providers": ["google"], "roles": {{roles}}, "signUp": { "mode": "open" } },
              "tenancy": {{tenancy}},
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """));

    [Fact]
    public void Self_service_without_tenancy_is_refused()
    {
        var error = Check("""{ "selfService": { "personalTenant": { "roles": [] } } }""").ShouldHaveSingleItem();

        error.Path.ShouldBe("/tenancy/selfService");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldBe("tenancy.selfService needs tenancy.enabled: true — self-service tenants are tenants.");
        error.FixSuggestion.ShouldBe("Set \"tenancy\": { \"enabled\": true } beside selfService, or remove selfService.");
    }

    [Fact]
    public void Self_service_with_tenancy_declined_by_value_is_refused() =>
        Check("""{ "enabled": false, "selfService": {} }""").ShouldHaveSingleItem().Path.ShouldBe("/tenancy/selfService");

    [Fact]
    public void Invitations_without_create_tenants_are_refused()
    {
        var error = Check("""{ "enabled": true, "selfService": { "invitations": true } }""").ShouldHaveSingleItem();

        error.Path.ShouldBe("/tenancy/selfService/invitations");
        error.Message.ShouldBe(
            "invitations let owners invite people into shared tenants, and without createTenants no shared tenant can exist.");
        error.FixSuggestion.ShouldBe("Add \"createTenants\": { \"creatorRoles\": [ … ] }, or remove invitations.");
    }

    [Fact]
    public void Invitations_declined_by_value_need_no_create_tenants() =>
        Check("""{ "enabled": true, "selfService": { "invitations": false } }""").ShouldBeEmpty();

    [Theory]
    [InlineData("admin")]
    [InlineData("authenticated")]
    [InlineData("anon")]
    public void A_built_in_assignable_role_is_refused(string role)
    {
        var error = Check($$"""{ "enabled": true, "selfService": { "assignableRoles": ["dispatcher", "{{role}}"] } }""")
            .ShouldHaveSingleItem();

        error.Path.ShouldBe("/tenancy/selfService/assignableRoles/1");
        error.Message.ShouldBe($"'{role}' is a built-in role, and a tenant role must be an application role from auth.roles.");
        error.FixSuggestion.ShouldNotBeNull().ShouldContain($"'{role}'");
    }

    /// <summary>A built-in role is refused as built-in even when <c>auth.roles</c> repeats its name.</summary>
    [Fact]
    public void A_built_in_role_listed_in_auth_roles_is_still_refused() =>
        Check("""{ "enabled": true, "selfService": { "assignableRoles": ["admin"] } }""", roles: """["admin"]""")
            .ShouldHaveSingleItem().Message.ShouldContain("built-in role");

    [Fact]
    public void An_undeclared_assignable_role_is_refused()
    {
        var error = Check("""{ "enabled": true, "selfService": { "assignableRoles": ["dispatcher", "owner"] } }""")
            .ShouldHaveSingleItem();

        error.Path.ShouldBe("/tenancy/selfService/assignableRoles/1");
        error.Message.ShouldBe("'owner' is not declared in auth.roles.");
        error.FixSuggestion.ShouldBe("Add 'owner' to auth.roles, or remove it from assignableRoles.");
    }

    [Fact]
    public void A_personal_or_creator_role_outside_assignable_roles_is_refused()
    {
        var errors = Check("""
            { "enabled": true, "selfService": {
              "personalTenant": { "roles": ["technician"] },
              "createTenants": { "creatorRoles": ["technician", "dispatcher"] },
              "assignableRoles": ["technician"] } }
            """);

        var error = errors.ShouldHaveSingleItem();
        error.Path.ShouldBe("/tenancy/selfService/createTenants/creatorRoles/1");
        error.Message.ShouldBe(
            "'dispatcher' is not in tenancy.selfService.assignableRoles, and a membership can only ever carry a tenant role.");
        error.FixSuggestion.ShouldBe("Add 'dispatcher' to assignableRoles, or remove it here.");

        Check("""{ "enabled": true, "selfService": { "personalTenant": { "roles": ["dispatcher"] } } }""")
            .ShouldHaveSingleItem().Path.ShouldBe("/tenancy/selfService/personalTenant/roles/0");
    }

    [Fact]
    public void Every_refusal_is_reported_together()
    {
        var errors = Check("""
            { "selfService": {
              "invitations": true,
              "assignableRoles": ["admin"] } }
            """);

        errors.Select(error => error.Path).ShouldBe(
        [
            "/tenancy/selfService",
            "/tenancy/selfService/invitations",
            "/tenancy/selfService/assignableRoles/0",
        ]);
    }

    /// <summary>A descriptor without <c>selfService</c> draws nothing, whatever its tenancy.</summary>
    [Theory]
    [InlineData("""{ "enabled": true }""")]
    [InlineData("""{ "enabled": false }""")]
    public void No_self_service_draws_nothing(string tenancy) => Check(tenancy).ShouldBeEmpty();

    /// <summary>§3's level-2 and level-3 descriptors, with <c>auth</c> as written there, apply without a refusal.</summary>
    [Fact]
    public void Spec_level_two_and_level_three_descriptors_are_accepted()
    {
        var levelTwo = AlvoDescriptor.Parse("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "level-two",
              "auth": { "providers": ["google"], "signUp": { "mode": "open" } },
              "tenancy": { "enabled": true, "selfService": { "personalTenant": { "roles": [] } } },
              "entities": {
                "time_entries": {
                  "fields": { "minutes": { "type": "integer", "required": true } },
                  "rules": { "list": "'authenticated' in @user.roles", "create": "'authenticated' in @user.roles" }
                }
              }
            }
            """);
        var levelThree = AlvoDescriptor.Parse("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "level-three",
              "auth": {
                "providers": ["google"],
                "roles": ["dispatcher", "technician"],
                "signUp": { "mode": "open" },
                "accessTokens": { "personal": true, "service": true }
              },
              "tenancy": {
                "enabled": true,
                "selfService": {
                  "createTenants": { "creatorRoles": ["dispatcher"], "maxPerAccount": 5 },
                  "invitations": true,
                  "assignableRoles": ["dispatcher", "technician"]
                }
              },
              "entities": {}
            }
            """);

        SelfServiceCheck.Check(levelTwo).ShouldBeEmpty();
        SelfServiceCheck.Check(levelThree).ShouldBeEmpty();
    }
}
