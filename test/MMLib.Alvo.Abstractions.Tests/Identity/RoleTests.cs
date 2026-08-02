namespace MMLib.Alvo.Abstractions.Tests.Identity;

public class RoleTests
{
    [Fact]
    public void Default_role_is_anon_so_a_forgotten_initialization_fails_safe()
    {
        default(Role).ShouldBe(Role.Anon);
        default(Role).Name.ShouldBe("anon");
    }

    [Fact]
    public void Default_role_hashes_like_anon()
    {
        default(Role).GetHashCode().ShouldBe(Role.Anon.GetHashCode());
    }

    [Fact]
    public void Built_in_roles_are_distinct_and_named()
    {
        Role.Authenticated.Name.ShouldBe("authenticated");
        Role.Admin.Name.ShouldBe("admin");
        Role.Admin.ShouldNotBe(Role.Authenticated);
        Role.Admin.ShouldNotBe(Role.Anon);
    }

    [Fact]
    public void Role_prints_as_its_name()
    {
        Role.Admin.ToString().ShouldBe("admin");
    }

    [Fact]
    public void Role_has_no_public_constructor_so_an_undeclared_role_cannot_be_minted()
    {
        typeof(Role).GetConstructors()
            .Where(constructor => constructor.GetParameters().Length > 0)
            .ShouldBeEmpty();
    }
}
