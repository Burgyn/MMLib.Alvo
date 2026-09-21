using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity.Internal;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// How the identity package composes with a real Alvo container — and, above all, what it does
/// <b>not</b> take over.
/// </summary>
public class AlvoIdentityRegistrationTests
{
    /// <summary>
    /// <b>The one fact that keeps a user id from being a credential.</b> The unkeyed
    /// <see cref="IAlvoContextResolver"/> is what the Data API hands the raw <c>X-Alvo-Api-Key</c>
    /// header to. The cookie resolver's "presented key" is a subject ASP.NET Core already
    /// authenticated, so if installing this package replaced that registration, anyone who knew an
    /// operator's uuid could present it as an API key and be authenticated as them.
    /// </summary>
    [Fact]
    public void Installing_identity_does_not_replace_the_api_key_resolver()
    {
        using var provider = Container();
        using var scope = provider.CreateScope();

        var unkeyed = scope.ServiceProvider.GetRequiredService<IAlvoContextResolver>();

        unkeyed.ShouldNotBeOfType<AlvoIdentityContextResolver>();
        unkeyed.GetType().Name.ShouldBe("ApiKeyContextResolver");
    }

    /// <summary>The cookie resolver is resolvable, but only through its own key.</summary>
    [Fact]
    public void The_cookie_resolver_is_reachable_only_under_its_key()
    {
        using var provider = Container();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey)
            .ShouldBeOfType<AlvoIdentityContextResolver>();
    }

    /// <summary>Installing the package takes over both identity ports the core defaults.</summary>
    [Fact]
    public void The_user_store_and_the_bootstrap_admin_come_from_the_package()
    {
        using var provider = Container();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IAlvoUserStore>().ShouldBeOfType<AlvoIdentityUserStore>();
        provider.GetRequiredService<IAlvoBootstrapAdmin>().ShouldBeOfType<AlvoBootstrapAdmin>();
    }

    /// <summary>
    /// <b><c>extensibility.md</c> rule 7, which names hosted services explicitly.</b>
    /// <c>AddHostedService&lt;T&gt;()</c> appends unconditionally, so a host that composed
    /// <c>AddAlvoIdentity</c> twice — one call in its own composition root, one inside a shared
    /// extension — would run the bootstrap seeding twice over one store.
    /// </summary>
    [Fact]
    public void Registering_twice_still_yields_exactly_one_bootstrap()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite("Data Source=:memory:"));
        services.AddAlvoIdentity(store => store.UseSqlite("Data Source=:memory:"));

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        provider.GetServices<IHostedService>().OfType<AlvoIdentityBootstrap>().ShouldHaveSingleItem();
    }

    /// <summary>
    /// <b><c>extensibility.md</c> rule 5.</b> The embedded distribution never goes through the
    /// standalone host's validation, so the package has to refuse its own misconfiguration or nobody
    /// does.
    /// </summary>
    [Fact]
    public void A_misconfigured_bootstrap_administrator_fails_the_start_rather_than_the_seeding()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(
            store => store.UseSqlite("Data Source=:memory:"),
            admin => admin.BootstrapEmail = "admin");

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<AlvoIdentityOptions>>().Value);
    }

    /// <summary>
    /// A real Alvo container with the identity package installed.
    /// </summary>
    /// <remarks>
    /// <c>ValidateScopes</c> is on, and deliberately: the store, the <c>DbContext</c> and the cookie
    /// resolver are <b>scoped</b> — a <c>DbContext</c> cannot be anything else — so a registration that
    /// accidentally made one of them a singleton would capture the context for the process's lifetime.
    /// Validating here is what makes that a failed fact instead of a leak a load test finds.
    /// </remarks>
    /// <returns>The built container.</returns>
    private static ServiceProvider Container()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvo(alvo => alvo.UseSqlite("Data Source=:memory:"));
        services.AddAlvoIdentity(store => store.UseSqlite("Data Source=:memory:"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }
}
