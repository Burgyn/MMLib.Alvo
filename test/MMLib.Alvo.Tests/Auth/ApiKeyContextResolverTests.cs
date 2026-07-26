using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Auth.Internal;
using MMLib.Alvo.Descriptor;
using MMLib.Alvo.Expressions.Internal;
using MMLib.Alvo.Rules;
using MMLib.Alvo.Rules.Internal;

namespace MMLib.Alvo.Tests.Auth;

public class ApiKeyContextResolverTests
{
    private static readonly Guid _user = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid _tenant = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static IAlvoContextResolver Resolver(Action<AlvoAuthOptions>? configure = null)
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = _user,
            Roles = { "authenticated", "editor" },
            Tenant = _tenant,
            Scopes = { "orders:read", "orders:write" },
        });
        configure?.Invoke(options);

        var store = new InMemoryApiKeyStore(Options.Create(options));
#pragma warning disable CA1859
        IAlvoContextResolver resolver = new ApiKeyContextResolver(
            store, RoleCatalog.Create(["editor"]), TimeProvider.System, new TenantResolver(), new PolicyCatalogProvider());
#pragma warning restore CA1859
        return resolver;
    }

    [Fact]
    public async Task A_valid_key_resolves_to_its_identity_roles_tenant_and_scopes()
    {
        var principal = await Resolver().ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull();
        principal.Context.User.ShouldBe(new UserId(_user));
        principal.Context.Roles.ShouldBe([Role.Authenticated, RoleCatalog.Create(["editor"]).Get("editor")], ignoreOrder: true);
        principal.Context.Tenant.ShouldBe(new TenantId(_tenant));
        principal.Scopes.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("dev")]
    [InlineData("dev.wrong")]
    [InlineData("unknown.s3cret")]
    [InlineData("dev.s3cret.extra")]
    public async Task Every_bad_credential_denies_rather_than_degrading(string? presented)
    {
        var principal = await Resolver().ResolveAsync(presented, requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task An_expired_key_denies()
    {
        var resolver = Resolver(options => options.DevKeys[0].ExpiresAt = DateTimeOffset.UnixEpoch);

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_naming_an_undeclared_role_denies_the_whole_request()
    {
        var options = new AlvoAuthOptions();
        options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = _user,
            Roles = { "edtior" },
            Scopes = { "orders:read" },
        });
        var resolver = new ApiKeyContextResolver(
            new InMemoryApiKeyStore(Options.Create(options)),
            RoleCatalog.Create(["editor"]),
            TimeProvider.System,
            new TenantResolver(),
            new PolicyCatalogProvider());

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task Requesting_another_tenant_than_the_key_owns_denies()
    {
        var principal = await Resolver().ResolveAsync(
            "dev.s3cret", requestedTenant: Guid.NewGuid().ToString(), TestContext.Current.CancellationToken);

        principal.ShouldBeNull();
    }

    [Fact]
    public async Task A_key_with_no_scopes_resolves_but_can_do_nothing()
    {
        var resolver = Resolver(options => options.DevKeys[0].Scopes.Clear());

        var principal = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull();
        new ScopeGate().Allows(principal, "orders", MMLib.Alvo.Rules.DataOperation.List).ShouldBeFalse();
    }

    /// <summary>
    /// The descriptor's <c>auth.roles</c> has to reach authentication, not only rule validation: a key
    /// granting an application role the applied project declares must resolve, and the very same key
    /// must resolve to nothing before anything is applied — the recognised role set is the built-ins
    /// until a descriptor widens it, so an unprimed host fails closed rather than minting a role
    /// nothing has declared.
    /// </summary>
    [Fact]
    public async Task A_role_the_applied_descriptor_declares_resolves_but_denies_before_the_apply()
    {
        using var host = HostGranting("editor");
        var resolver = host.GetRequiredService<IAlvoContextResolver>();

        var beforeApply = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        beforeApply.ShouldBeNull();

        Apply(host, DescriptorDeclaringRoles("editor"));
        var afterApply = await resolver.ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

        afterApply.ShouldNotBeNull();
        afterApply.Context.Roles.Select(role => role.Name).ShouldContain("editor");
    }

    /// <summary>
    /// The mirror case, and the reason the descriptor rather than the injected catalog is
    /// authoritative once a project is applied: a role the applied descriptor does <em>not</em>
    /// declare stays unresolvable, so removing a role from <c>auth.roles</c> takes effect on the very
    /// next request instead of only after a restart.
    /// </summary>
    [Fact]
    public async Task A_role_the_applied_descriptor_does_not_declare_still_denies()
    {
        using var host = HostGranting("editor");
        Apply(host, DescriptorDeclaringRoles("reviewer"));

        var principal = await ResolveDevKeyAsync(host);

        principal.ShouldBeNull();
    }

    private static async Task<AlvoPrincipal?> ResolveDevKeyAsync(ServiceProvider host) =>
        await host.GetRequiredService<IAlvoContextResolver>()
            .ResolveAsync("dev.s3cret", requestedTenant: null, TestContext.Current.CancellationToken);

    private static ServiceProvider HostGranting(string roleName)
    {
        var services = new ServiceCollection();
        services.Configure<AlvoAuthOptions>(options => options.DevKeys.Add(new AlvoDevApiKey
        {
            KeyId = "dev",
            Secret = "s3cret",
            User = _user,
            Roles = { roleName },
            Scopes = { "orders:read" },
        }));
        services.AddAlvo();
        return services.BuildServiceProvider();
    }

    private static void Apply(ServiceProvider host, string descriptorJson)
    {
        var descriptor = AlvoDescriptor.Parse(descriptorJson);
        PolicyCatalogPriming.Prime(
            host.GetRequiredService<IPolicyCatalogProvider>(),
            new CelCompiler(),
            descriptor.Name,
            descriptor,
            DescriptorToSchemaMapper.Map(descriptor));
    }

    private static string DescriptorDeclaringRoles(params string[] roleNames) => $$"""
    {
      "apiVersion": "alvo.dev/v1",
      "name": "demo",
      "auth": { "roles": [{{string.Join(", ", roleNames.Select(name => $"\"{name}\""))}}] },
      "entities": {
        "orders": {
          "fields": { "title": { "type": "string" } }
        }
      }
    }
    """;

    [Fact]
    public void Authentication_consults_the_DI_registered_TenantResolver_not_a_hard_wired_one()
    {
        var services = new ServiceCollection();
        var registeredTenantResolver = new TenantResolver();
        services.AddSingleton(registeredTenantResolver);
        services.AddAlvo();

        using var provider = services.BuildServiceProvider();
        var resolver = (ApiKeyContextResolver)provider.GetRequiredService<IAlvoContextResolver>();

        resolver.TenantResolver.ShouldBeSameAs(registeredTenantResolver);
    }
}
