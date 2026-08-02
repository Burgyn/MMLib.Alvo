using MMLib.Alvo.Descriptor;

namespace MMLib.Alvo.Abstractions.Tests.Identity;

public class RoleCatalogTests
{
    private const string DescriptorWithRoles = """
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "auth": { "roles": ["editor", "compliance"] },
      "entities": {
        "orders": { "fields": { "title": { "type": "string" } } }
      }
    }
    """;

    [Fact]
    public void Catalog_always_contains_the_three_built_ins()
    {
        RoleCatalog.BuiltInOnly.All.ShouldBe([Role.Anon, Role.Authenticated, Role.Admin], ignoreOrder: true);
    }

    [Fact]
    public void Catalog_mints_declared_application_roles()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        catalog.TryGet("editor", out var editor).ShouldBeTrue();
        editor.Name.ShouldBe("editor");
        catalog.All.Count.ShouldBe(5);
    }

    [Fact]
    public void Undeclared_role_is_rejected_loudly_with_the_known_names()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        var exception = Should.Throw<UnknownRoleException>(() => catalog.Get("edtior"));

        exception.RoleName.ShouldBe("edtior");
        exception.Message.ShouldContain("editor");
    }

    [Fact]
    public void Resolving_a_set_of_names_yields_roles()
    {
        var catalog = RoleCatalog.FromDescriptor(AlvoDescriptor.Parse(DescriptorWithRoles));

        catalog.Resolve(["authenticated", "editor"])
            .ShouldBe([Role.Authenticated, catalog.Get("editor")], ignoreOrder: true);
    }

    [Fact]
    public void A_descriptor_role_colliding_with_a_built_in_does_not_duplicate_it()
    {
        var catalog = RoleCatalog.Create(["admin", "editor"]);

        catalog.All.Count.ShouldBe(4);
        catalog.Get("admin").ShouldBe(Role.Admin);
    }
}
