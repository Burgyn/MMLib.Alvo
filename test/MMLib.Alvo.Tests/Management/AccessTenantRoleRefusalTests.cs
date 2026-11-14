using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Descriptor.Internal;

namespace MMLib.Alvo.Tests.Management;

/// <summary>
/// <b>A tenant role never manages the project</b> (spec §3.1, §6.2): anyone who creates a tenant holds the creator's
/// roles there, so an <c>access</c> level naming one of <c>tenancy.selfService.assignableRoles</c> is refused at apply.
/// A declared role that is not assignable keeps managing, as it always has.
/// </summary>
public sealed class AccessTenantRoleRefusalTests
{
    private static DescriptorValidationResult Validate(string access, string assignable = """["dispatcher", "technician"]""") =>
        new DescriptorValidator().Validate($$"""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "access-tenant-roles",
              "auth": { "providers": ["google"], "roles": ["dispatcher", "technician", "owner"], "signUp": { "mode": "invitationOnly" } },
              "tenancy": { "enabled": true, "selfService": { "createTenants": { "creatorRoles": ["dispatcher"] }, "assignableRoles": {{assignable}} } },
              "access": {{access}},
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);

    [Theory]
    [InlineData("admin")]
    [InlineData("developer")]
    [InlineData("viewer")]
    public void An_assignable_role_in_any_access_level_is_refused(string level)
    {
        var result = Validate($$"""{ "{{level}}": "'technician' in @user.roles" }""");

        var error = result.Errors.ShouldHaveSingleItem();
        error.Path.ShouldBe($"/access/{level}");
        error.Severity.ShouldBe(DescriptorValidationSeverity.Error);
        error.Message.ShouldBe(
            "'technician' is a tenant role (tenancy.selfService.assignableRoles), and a tenant role can never manage the project.");
        error.FixSuggestion.ShouldBe(
            "Admit a project role here — one an operator or an API key grants — or remove 'technician' from assignableRoles.");
    }

    /// <summary>
    /// <b><c>owner</c> as an ordinary application role keeps working</b> (spec §6.2): declared, not assignable, so it
    /// manages exactly as it did before self-service existed.
    /// </summary>
    [Fact]
    public void A_declared_role_that_is_not_assignable_still_manages() =>
        Validate("""{ "admin": "'owner' in @user.roles", "viewer": "'admin' in @user.roles" }""").Errors.ShouldBeEmpty();

    [Fact]
    public void The_refusal_holds_when_the_role_is_reached_through_a_disjunction()
    {
        var error = Validate("""{ "developer": "'owner' in @user.roles || 'dispatcher' in @user.roles" }""")
            .Errors.ShouldHaveSingleItem();

        error.Path.ShouldBe("/access/developer");
        error.Message.ShouldStartWith("'dispatcher' is a tenant role");
    }

    /// <summary>Without <c>selfService</c> there is no tenant role, so the same level is accepted.</summary>
    [Fact]
    public void Without_self_service_the_same_role_manages()
    {
        var result = new DescriptorValidator().Validate("""
            {
              "apiVersion": "alvo.dev/v1",
              "name": "access-project-roles",
              "auth": { "roles": ["dispatcher"] },
              "access": { "admin": "'dispatcher' in @user.roles" },
              "entities": { "jobs": { "fields": { "title": { "type": "string" } } } }
            }
            """);

        result.Errors.ShouldBeEmpty();
    }

    /// <summary>An undeclared literal is refused once, as undeclared — not a second time as a tenant role.</summary>
    [Fact]
    public void An_undeclared_literal_is_refused_as_undeclared_only() =>
        Validate("""{ "admin": "'ghost' in @user.roles" }""")
            .Errors.ShouldHaveSingleItem().Message.ShouldNotContain("tenant role");
}
