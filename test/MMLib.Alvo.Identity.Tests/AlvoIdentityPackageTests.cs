namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// The package's two wire-level names, pinned as literals so a rename is a visible act.
/// </summary>
/// <remarks>
/// Neither is cosmetic. The section name is what an operator types into a container's environment,
/// and the resolver key is what keeps the cookie resolver off the <c>X-Alvo-Api-Key</c> path — a
/// silent rename of either is a working deployment that stops working, or a header path that starts
/// resolving a credential it must not.
/// </remarks>
public class AlvoIdentityPackageTests
{
    [Fact]
    public void The_configuration_section_is_the_Alvo_spelling_the_host_already_binds()
        => AlvoIdentity.ConfigurationSection.ShouldBe("Alvo:Admin");

    [Fact]
    public void The_resolver_key_is_stable()
        => AlvoIdentity.ResolverKey.ShouldBe("alvo-identity");
}
