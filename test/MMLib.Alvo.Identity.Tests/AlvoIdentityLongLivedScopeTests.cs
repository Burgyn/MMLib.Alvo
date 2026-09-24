using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Identity.Internal;
using NSubstitute;

namespace MMLib.Alvo.Identity.Tests;

/// <summary>
/// <b>A revocation is seen by a scope that was already open when it was written.</b>
/// </summary>
/// <remarks>
/// <para>
/// A Blazor circuit is one DI scope for as long as the tab stays open, so the dashboard's
/// <c>DbContext</c> — and its change tracker — lives that long too. A store that reads through a
/// tracked lookup answers every later call from the identity map, and a disable or a tenant move
/// written by another administrator's scope is never seen: the disabled operator keeps acting, and a
/// moved one keeps acting in the old tenant. Evidence:
/// <c>docs/superpowers/specs/evidence/2026-09-25-disabled-operator-stale-identity.md</c>.
/// </para>
/// <para>
/// <b>Two scopes over one SQLite file, deliberately.</b> <see cref="AlvoIdentityUserStoreTests"/>
/// uses one scope for the whole class, so it writes and reads through the same tracker and cannot
/// show this. Scope A stands for the open circuit; each write happens in a fresh scope B, the way a
/// second administrator's request would.
/// </para>
/// </remarks>
public sealed class AlvoIdentityLongLivedScopeTests : IAsyncLifetime
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"alvo-identity-{Guid.NewGuid():N}.db");
    private ServiceProvider _provider = null!;
    private IServiceScope _circuit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}"));

        /* The catalogue is the applied descriptor's, which this package does not own; a fixed one
           keeps the facts about the store rather than about applying a descriptor. */
        var catalog = Substitute.For<IRoleCatalogProvider>();
        catalog.DeclaredRoles.Returns(RoleCatalog.Create(["editor"]));
        services.AddSingleton(catalog);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _circuit = _provider.CreateScope();

        await _circuit.ServiceProvider.GetRequiredService<AlvoIdentityDbContext>()
            .Database.EnsureCreatedAsync(Ct);
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _circuit.Dispose();
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_file);
    }

    /// <summary>The open scope's id lookup sees a disable written by another scope.</summary>
    [Fact]
    public async Task A_disable_written_elsewhere_is_seen_by_an_open_scopes_id_lookup()
    {
        var id = await CreateAsync();
        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        (await store.FindAsync(id, Ct)).ShouldNotBeNull().IsDisabled.ShouldBeFalse();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await store.FindAsync(id, Ct)).ShouldNotBeNull().IsDisabled.ShouldBeTrue(
            "a disable takes effect at once; an answer from the change tracker is a cache nobody asked for");
    }

    /// <summary>The open scope's email lookup sees a disable written by another scope.</summary>
    [Fact]
    public async Task A_disable_written_elsewhere_is_seen_by_an_open_scopes_email_lookup()
    {
        var id = await CreateAsync();
        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        (await store.FindByEmailAsync("Eva@Example.test", Ct)).ShouldNotBeNull().IsDisabled.ShouldBeFalse();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await store.FindByEmailAsync("eva@example.TEST", Ct)).ShouldNotBeNull().IsDisabled.ShouldBeTrue();
    }

    /// <summary>The open scope sees a tenant move written by another scope.</summary>
    [Fact]
    public async Task A_tenant_move_written_elsewhere_is_seen_by_an_open_scope()
    {
        var id = await CreateAsync();
        var moved = TenantId.New();
        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        (await store.FindAsync(id, Ct)).ShouldNotBeNull().Tenant.ShouldBeNull();

        await AdministerAsync(people => people.SetTenantAsync(id, moved, Ct));

        (await store.FindAsync(id, Ct)).ShouldNotBeNull().Tenant.ShouldBe(moved);
    }

    /// <summary>
    /// End to end through the keyed resolver the dashboard's caller resolution uses: a disabled
    /// operator stops resolving in the scope that already resolved them.
    /// </summary>
    [Fact]
    public async Task An_open_scopes_resolver_refuses_an_operator_disabled_elsewhere()
    {
        var id = await CreateAsync();
        var resolver = CookieResolver();
        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldBeNull(
            "a disabled operator must be refused on their very next call, not on their next page load");
    }

    /// <summary>
    /// The tenancy half: an operator moved to another tenant acts in the new one from the scope that
    /// already resolved them, never in the old one.
    /// </summary>
    [Fact]
    public async Task An_open_scopes_resolver_mints_the_tenant_an_operator_was_moved_to()
    {
        var id = await CreateAsync(TenantId.New());
        var moved = TenantId.New();
        var resolver = CookieResolver();
        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull();

        await AdministerAsync(people => people.SetTenantAsync(id, moved, Ct));

        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull().Context.Tenant.ShouldBe(
            moved, "a caller acting in the tenant they were moved out of is a cross-tenant write");
    }

    /// <summary>
    /// A write made through the open scope itself still works after that scope has read the row —
    /// the fresh reads must not leave the write path with a detached or conflicting entity.
    /// </summary>
    [Fact]
    public async Task The_open_scope_can_still_write_the_row_it_has_read()
    {
        var id = await CreateAsync();
        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        await store.FindAsync(id, Ct);

        await store.SetRolesAsync(id, ["editor"], Ct);

        (await store.FindAsync(id, Ct)).ShouldNotBeNull().RoleNames.ShouldBe(["editor"]);
    }

    /// <summary>The cookie resolver, resolved from the long-lived scope.</summary>
    /// <returns>The resolver the dashboard's caller resolution reaches.</returns>
    private IAlvoContextResolver CookieResolver()
        => _circuit.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey);

    /// <summary>Creates an operator holding <c>editor</c>, from a scope of its own.</summary>
    /// <param name="tenant">The tenant to grant, if any.</param>
    /// <returns>The new operator's identifier.</returns>
    private async Task<UserId> CreateAsync(TenantId? tenant = null)
    {
        UserId id = default;
        await AdministerAsync(async people =>
            id = (await people.CreateAsync(new AlvoUserCreation("Eva@Example.test", ["editor"], tenant), Ct)).Id);
        return id;
    }

    /// <summary>
    /// Runs one administration call in a fresh scope — the stand-in for a second administrator's
    /// request, which never shares the open circuit's change tracker.
    /// </summary>
    /// <param name="write">The call to make.</param>
    /// <returns>A task that completes when the scope has been disposed.</returns>
    private async Task AdministerAsync(Func<IAlvoUserAdministration, Task> write)
    {
        using var other = _provider.CreateScope();
        await write(other.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey));
    }
}
