using Microsoft.Extensions.DependencyInjection;

namespace MMLib.Alvo.Tests.Auth;

/// <summary>
/// The core's own answer to "is this caller the bootstrap admin" — which is "no", for everyone,
/// until <c>MMLib.Alvo.Identity</c> replaces it.
/// </summary>
/// <remarks>
/// The direction is the fact. #146 lets the bootstrap admin bypass the descriptor's <c>access</c>
/// block entirely, so a default that answered "yes" — or that was simply absent and left the gate
/// to resolve <see langword="null"/> — would be an unauthenticated bypass of the whole management
/// surface in every host that does not install the identity package.
/// </remarks>
public class BootstrapAdminTests
{
    private static readonly UserId _known = UserId.New();

    [Fact]
    public void A_host_without_the_identity_package_has_no_bootstrap_admin()
    {
        var services = new ServiceCollection();
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();
        var bootstrap = provider.GetRequiredService<IAlvoBootstrapAdmin>();

        bootstrap.IsBootstrapAdmin(UserId.New()).ShouldBeFalse();
        bootstrap.IsBootstrapAdmin(default).ShouldBeFalse(
            "the all-zero UserId means 'no identity', and it must never be the one that bypasses access");
        bootstrap.IsBootstrapAdmin(AlvoContext.System(null).User).ShouldBeFalse(
            "the framework's own system identity runs post-commit work; it is not an operator");
    }

    [Fact]
    public void A_host_may_replace_the_default_bootstrap_admin()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAlvoBootstrapAdmin>(new OnlyThisUserIsAdmin(_known));
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IAlvoBootstrapAdmin>().IsBootstrapAdmin(_known).ShouldBeTrue();
    }

    private sealed class OnlyThisUserIsAdmin(UserId admin) : IAlvoBootstrapAdmin
    {
        public bool IsBootstrapAdmin(UserId user) => user == admin;
    }
}
