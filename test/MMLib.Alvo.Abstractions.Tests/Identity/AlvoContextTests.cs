namespace MMLib.Alvo.Abstractions.Tests.Identity;

public class AlvoContextTests
{
    [Fact]
    public void Anonymous_context_holds_exactly_the_anon_role_and_no_tenant()
    {
        AlvoContext.Anonymous.Roles.ShouldBe([Role.Anon]);
        AlvoContext.Anonymous.Tenant.ShouldBeNull();
    }

    [Fact]
    public void An_empty_role_set_is_rejected_because_anonymous_is_a_role_not_an_absence()
    {
        Should.Throw<ArgumentException>(() => new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role>(),
        });
    }

    [Fact]
    public void An_empty_role_set_is_rejected_through_a_with_expression_too()
    {
        Should.Throw<ArgumentException>(() => AlvoContext.Anonymous with { Roles = new HashSet<Role>() });
    }

    [Fact]
    public void HasRole_answers_over_the_whole_set()
    {
        var context = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated, Role.Admin },
        };

        context.HasRole(Role.Admin).ShouldBeTrue();
        context.HasRole(Role.Anon).ShouldBeFalse();
    }

    [Fact]
    public void System_context_names_its_identity_for_post_commit_paths()
    {
        var tenant = new TenantId(Guid.NewGuid());

        var system = AlvoContext.System(tenant);

        system.Roles.ShouldContain(Role.Admin);
        system.Tenant.ShouldBe(tenant);
        system.User.ShouldBe(AlvoContext.System(null).User);
    }

    [Fact]
    public void A_context_carries_no_tenant_roles_unless_given_some()
    {
        AlvoContext.Anonymous.TenantRoles.ShouldBeEmpty();
        AlvoContext.System(TenantId.New()).TenantRoles.ShouldBeEmpty();
        new AlvoContext { User = UserId.New(), Roles = new HashSet<Role> { Role.Authenticated } }
            .TenantRoles.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("anon")]
    [InlineData("authenticated")]
    [InlineData("admin")]
    public void A_built_in_role_is_never_a_tenant_role(string builtIn)
    {
        var tenantRoles = RoleCatalog.BuiltInOnly.Resolve([builtIn]);

        var created = Should.Throw<ArgumentException>(() => new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            TenantRoles = tenantRoles,
        });
        var copied = Should.Throw<ArgumentException>(() => AlvoContext.Anonymous with { TenantRoles = tenantRoles });

        created.ParamName.ShouldBe("TenantRoles");
        created.Message.ShouldStartWith(
            "A tenant role is never a built-in role; anon, authenticated and admin are project roles only.");
        copied.ParamName.ShouldBe("TenantRoles");
    }

    [Fact]
    public void Null_tenant_roles_are_refused()
    {
        Should.Throw<ArgumentNullException>(() => AlvoContext.Anonymous with { TenantRoles = null! });
    }

    [Fact]
    public void A_with_copy_keeps_the_tenant_roles()
    {
        var dispatcher = RoleCatalog.Create(["dispatcher"]).Resolve(["dispatcher"]);
        var context = new AlvoContext
        {
            User = UserId.New(),
            Roles = new HashSet<Role> { Role.Authenticated },
            TenantRoles = dispatcher,
        };

        var copy = context with { Tenant = TenantId.New() };

        copy.TenantRoles.ShouldBeSameAs(dispatcher);
        copy.HasRole(dispatcher.Single()).ShouldBeFalse();
    }
}
