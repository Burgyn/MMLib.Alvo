using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity.Internal;
using NSubstitute;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// Minting an <see cref="AlvoContext"/> from a signed-in person: which roles they receive, and
/// every way this refuses to mint one at all.
/// </summary>
/// <remarks>
/// <b>The intersection is the load-bearing fact of the whole package.</b> Membership outlives a
/// descriptor edit — a row saying "Eva is an editor" survives the revision that deleted
/// <c>editor</c> from <c>auth.roles</c> — so a resolver that minted every stored name would grant a
/// role the rules were never compiled against, which is exactly the inconsistency
/// <see cref="IRoleCatalogProvider"/>'s own remarks call "one descriptor, one catalog, one guard".
/// </remarks>
public class AlvoIdentityContextResolverTests
{
    private static readonly UserId _eva = UserId.New();

    [Fact]
    public async Task A_role_the_descriptor_does_not_declare_is_not_minted()
    {
        var resolver = Resolver(
            stored: User(_eva, "editor", "ghost"),
            declared: RoleCatalog.Create(["editor"]));

        var principal = await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken);

        var roles = principal.ShouldNotBeNull().Context.Roles.Select(role => role.Name).ToList();
        roles.ShouldContain("editor");
        roles.ShouldNotContain(
            "ghost",
            "a membership row outlives the descriptor revision that deleted the role, and minting it "
            + "would grant a role no rule was ever compiled against");
    }

    [Fact]
    public async Task Every_signed_in_caller_also_holds_the_authenticated_role()
    {
        var resolver = Resolver(stored: User(_eva), declared: RoleCatalog.Create(["editor"]));

        var principal = await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull().Context.HasRole(Role.Authenticated).ShouldBeTrue(
            "a user with no application role at all still has a non-empty role set, which AlvoContext requires");
    }

    [Fact]
    public async Task A_null_role_catalogue_refuses_rather_than_minting()
    {
        var resolver = Resolver(stored: User(_eva, "editor"), declared: null);

        var principal = await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken);

        principal.ShouldBeNull(
            "DeclaredRoles is null until a descriptor is applied, and the port's contract is to refuse "
            + "an application role rather than mint one nothing has declared");
    }

    [Fact]
    public async Task An_unknown_user_refuses()
    {
        var resolver = Resolver(stored: null, declared: RoleCatalog.Create(["editor"]));

        (await resolver.ResolveAsync(UserId.New().ToString(), null, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task A_disabled_user_refuses()
    {
        var resolver = Resolver(
            stored: User(_eva, "editor") with { IsDisabled = true },
            declared: RoleCatalog.Create(["editor"]));

        (await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_subject_that_is_not_a_real_user_id_refuses(string? subject)
    {
        var resolver = Resolver(stored: User(_eva, "editor"), declared: RoleCatalog.Create(["editor"]));

        (await resolver.ResolveAsync(subject, null, TestContext.Current.CancellationToken))
            .ShouldBeNull("the all-zero uuid is reserved to mean 'no identity' and must never be minted");
    }

    [Fact]
    public async Task A_session_may_not_ask_to_act_in_a_tenant()
    {
        var resolver = Resolver(stored: User(_eva, "editor"), declared: RoleCatalog.Create(["editor"]));

        (await resolver.ResolveAsync(_eva.ToString(), TenantId.New().ToString(), TestContext.Current.CancellationToken))
            .ShouldBeNull(
                "a cookie session carries no tenant grant of its own, so honouring a requested tenant "
                + "would let the caller choose the tenant it acts in");
    }

    [Fact]
    public async Task A_session_principal_is_scoped_to_every_entity_because_the_rules_govern_it()
    {
        var resolver = Resolver(stored: User(_eva, "editor"), declared: RoleCatalog.Create(["editor"]));

        var principal = await resolver.ResolveAsync(_eva.ToString(), null, TestContext.Current.CancellationToken);

        principal.ShouldNotBeNull().Scopes.ShouldBe(
            [
                new ApiKeyScope { Entity = "*", Access = ScopeAccess.Read },
                new ApiKeyScope { Entity = "*", Access = ScopeAccess.Write },
            ],
            ignoreOrder: true,
            "scopes narrow an API key; a person's authority is the descriptor's rules, and an empty "
            + "scope set would deny every operation before any rule ran");
    }

    private static AlvoUser User(UserId id, params string[] roleNames) =>
        new() { Id = id, Email = "eva@example.test", RoleNames = roleNames };

    private static AlvoIdentityContextResolver Resolver(AlvoUser? stored, RoleCatalog? declared)
    {
        var users = Substitute.For<IAlvoUserStore>();

        // CA2012 reads the call as consuming a ValueTask. Configuring a substitute does not consume
        // one — the call is recorded, never awaited — and the only alternative is a hand-written fake
        // whose whole body would be this expression.
#pragma warning disable CA2012
        users.FindAsync(Arg.Any<UserId>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<AlvoUser?>(
                stored is not null && stored.Id == call.Arg<UserId>() ? stored : null));
#pragma warning restore CA2012

        var catalogue = Substitute.For<IRoleCatalogProvider>();
        catalogue.DeclaredRoles.Returns(declared);

        return new AlvoIdentityContextResolver(users, catalogue);
    }
}
