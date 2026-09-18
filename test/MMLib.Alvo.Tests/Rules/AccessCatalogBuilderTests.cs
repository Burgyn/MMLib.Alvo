using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Rules;

namespace MMLib.Alvo.Tests.Rules;

/// <summary>
/// The descriptor's <c>access</c> block, compiled in the same pass as every rule and judged against
/// the same declared roles.
/// </summary>
/// <remarks>
/// <b>One pass, one catalogue, one <see cref="RoleCatalog"/>.</b> Compiling the levels separately —
/// lazily, or from a second holder — is how the levels and the rules would come to be judged against
/// two descriptor revisions, which is the inconsistency <c>IRoleCatalogProvider</c>'s remarks call
/// "one descriptor, one catalog, one guard".
/// </remarks>
public class AccessCatalogBuilderTests
{
    [Fact]
    public void Every_declared_level_compiles()
    {
        var catalog = Build(AccessBlock(
            admin: "'manager' in @user.roles",
            developer: "'editor' in @user.roles",
            viewer: "'editor' in @user.roles || 'manager' in @user.roles"));

        catalog.ManagementAccess.Admin.ShouldNotBeNull();
        catalog.ManagementAccess.Developer.ShouldNotBeNull();
        catalog.ManagementAccess.Viewer.ShouldNotBeNull();
    }

    [Fact]
    public void A_descriptor_with_no_access_block_compiles_to_an_empty_catalogue()
    {
        var catalog = Build(access: null);

        catalog.ManagementAccess.IsEmpty.ShouldBeTrue(
            "no access block means only the bootstrap admin can manage the project — default-deny, "
            + "and usable: the first-run wizard works and nobody else gets in until the descriptor says so");
    }

    [Fact]
    public void A_level_the_descriptor_declares_nothing_for_stays_null()
    {
        var catalog = Build(AccessBlock(admin: "'manager' in @user.roles"));

        catalog.ManagementAccess.Admin.ShouldNotBeNull();
        catalog.ManagementAccess.Developer.ShouldBeNull();
        catalog.ManagementAccess.Viewer.ShouldBeNull();
        catalog.ManagementAccess.IsEmpty.ShouldBeFalse();
    }

    /// <summary>
    /// <b>The second half of the role-literal check.</b> A typo'd literal compiles and type-checks
    /// perfectly and then simply never matches, so a level written to admit managers admits nobody —
    /// and, negated, everybody. Rules already get this check; levels now get the same one, from the same
    /// walk.
    /// </summary>
    [Fact]
    public void An_undeclared_role_literal_is_refused_at_apply_naming_the_level()
    {
        var errors = Refuse(AccessBlock(admin: "'amdin' in @user.roles"));

        errors.ShouldContain(error => error.Path == "/access/admin");
        errors.ShouldContain(error => error.Message.Contains("'amdin'", StringComparison.Ordinal));
        errors.ShouldContain(
            error => error.FixSuggestion!.Contains("manager", StringComparison.Ordinal),
            "the same 'did you mean' shape an unknown field, enum value or role literal already gets");
    }

    [Fact]
    public void An_undeclared_role_literal_is_refused_on_every_level_not_only_admin()
    {
        Refuse(AccessBlock(developer: "'amdin' in @user.roles"))
            .ShouldContain(error => error.Path == "/access/developer");
        Refuse(AccessBlock(viewer: "'amdin' in @user.roles"))
            .ShouldContain(error => error.Path == "/access/viewer");
    }

    [Fact]
    public void A_level_naming_a_row_field_is_refused_at_apply()
    {
        var errors = Refuse(AccessBlock(admin: "owner_id == @user.id"));

        errors.ShouldContain(error => error.Path == "/access/admin");
        errors.ShouldContain(error => error.Message.Contains("no row", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_level_that_is_not_a_predicate_is_refused_at_apply()
        => Refuse(AccessBlock(viewer: "'manager'"))
            .ShouldContain(error => error.Path == "/access/viewer");

    [Fact]
    public void Every_level_is_reported_rather_than_only_the_first()
    {
        var errors = Refuse(AccessBlock(
            admin: "'amdin' in @user.roles",
            developer: "'edtior' in @user.roles",
            viewer: "'salse' in @user.roles"));

        errors.Select(error => error.Path).Distinct()
            .ShouldBe(["/access/admin", "/access/developer", "/access/viewer"], ignoreOrder: true,
                "an agent fixing a descriptor sees every fix it needs in one round trip");
    }

    [Fact]
    public void A_built_in_role_literal_is_declared_without_appearing_in_auth_roles()
        => Build(AccessBlock(admin: "'admin' in @user.roles")).ManagementAccess.Admin.ShouldNotBeNull();

    /// <summary>
    /// <b>A level is refused by the same apply that refuses a rule, not by a second one.</b> The whole
    /// catalogue is null when either fails, so an access typo can never leave a half-applied descriptor
    /// whose rules are live and whose levels are not.
    /// </summary>
    [Fact]
    public void A_bad_level_and_a_bad_rule_are_reported_by_one_apply()
    {
        var errors = PolicyCatalogBuilderProbe.Refuse(
            AccessBlock(admin: "'amdin' in @user.roles"),
            listRule: "'edtior' in @user.roles");

        errors.ShouldContain(error => error.Path == "/access/admin");
        errors.ShouldContain(error => error.Path == "/entities/deals/rules/list");
    }

    private static Access AccessBlock(string? admin = null, string? developer = null, string? viewer = null) =>
        new() { Admin = admin, Developer = developer, Viewer = viewer };

    private static PolicyCatalog Build(Access? access) => PolicyCatalogBuilderProbe.Build(access);

    private static IReadOnlyList<DescriptorValidationError> Refuse(Access? access) =>
        PolicyCatalogBuilderProbe.Refuse(access);
}
