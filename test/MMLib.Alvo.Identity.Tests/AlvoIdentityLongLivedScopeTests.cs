using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;
using MMLib.Alvo.Data;
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
    private readonly InterleavedWrite _interleaved = new();
    private ServiceProvider _provider = null!;
    private IServiceScope _circuit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlvoIdentity(store => store.UseSqlite($"Data Source={_file}").AddInterceptors(_interleaved));

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

    /// <summary>
    /// A role revoked by another scope is not minted by the scope that already resolved the operator —
    /// the roles half of the revocation story, beside the disable and the tenant move above.
    /// </summary>
    [Fact]
    public async Task A_role_revoked_elsewhere_is_not_minted_by_an_open_scope()
    {
        var id = await CreateAsync();
        var resolver = CookieResolver();
        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull()
            .Context.Roles.Select(role => role.Name).ShouldContain("editor");

        await AdministerAsync(people => people.SetRolesAsync(id, [], Ct));

        (await resolver.ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull()
            .Context.Roles.Select(role => role.Name).ShouldNotContain(
                "editor", "a revoked role is revoked on the next call, not on the next page load");
    }

    /// <summary>
    /// <b>A write from the open scope is made against the stored row, not against the copy that scope
    /// last wrote.</b> Before each administration call was its own unit of work, the row stayed in the
    /// circuit's change tracker after the first write, so the next write sent that copy's concurrency
    /// stamp and was refused for a change the operator never saw — the stamp checked was whatever the
    /// tab first touched, not what its screen showed (the list is an untracked read).
    /// </summary>
    [Fact]
    public async Task An_open_scopes_write_is_made_against_the_stored_row_not_the_one_it_last_wrote()
    {
        var id = await CreateAsync();
        var moved = TenantId.New();
        var tab = OpenScopeAdministration();
        await tab.SetRolesAsync(id, [], Ct);
        await AdministerAsync(people => people.SetTenantAsync(id, moved, Ct));

        await tab.SetDisabledAsync(id, disabled: true, Ct);

        var stored = await StoredAsync(id);
        stored.IsDisabled.ShouldBeTrue();
        stored.Tenant.ShouldBe(moved, "the other administrator's write is kept, not overwritten by the tab's copy");
    }

    /// <summary>The same unit-of-work rule for the membership store's own role write.</summary>
    [Fact]
    public async Task The_open_scopes_store_replaces_roles_on_the_stored_row_not_the_one_it_last_wrote()
    {
        var id = await CreateAsync();
        var store = _circuit.ServiceProvider.GetRequiredService<IAlvoUserStore>();
        await store.SetRolesAsync(id, [], Ct);
        await AdministerAsync(people => people.SetTenantAsync(id, TenantId.New(), Ct));

        await store.SetRolesAsync(id, ["editor"], Ct);

        (await StoredAsync(id)).RoleNames.ShouldBe(["editor"]);
    }

    /// <summary>
    /// <b>A write that loses a race is a clean, named refusal, and it leaves nothing behind.</b> Another
    /// administrator's write lands between the open scope's read and its save, so the concurrency stamp
    /// check fails — fail-safe, and correct. What must not follow is the failed row staying in the
    /// circuit's change tracker: every later identity write from that tab would re-flush it and fail,
    /// so the tab could no longer disable <em>anybody</em> until it was reloaded.
    /// </summary>
    [Fact]
    public async Task A_write_that_loses_a_race_is_refused_cleanly_and_the_open_scope_can_still_disable_someone_else()
    {
        var eva = await CreateAsync();
        var otto = await CreateAsync(email: "Otto@Example.test");
        var moved = TenantId.New();
        var tab = OpenScopeAdministration();

        _interleaved.Before(() => AdministerAsync(people => people.SetTenantAsync(eva, moved, Ct)));
        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => tab.SetDisabledAsync(eva, disabled: true, Ct));

        _interleaved.Before(() => AdministerAsync(people => people.SetTenantAsync(eva, moved, Ct)));
        await Should.ThrowAsync<AlvoPreconditionFailedException>(() => tab.SetDisabledAsync(eva, disabled: true, Ct));

        await tab.SetDisabledAsync(otto, disabled: true, Ct);

        (await StoredAsync(otto)).IsDisabled.ShouldBeTrue("a refused write must not stop the tab from revoking somebody else");
        var loser = await StoredAsync(eva);
        loser.IsDisabled.ShouldBeFalse("the refused write wrote nothing");
        loser.Tenant.ShouldBe(moved);
    }

    /// <summary>
    /// <b>Locked out is not disabled.</b> Sign-in locks an account after repeated failures, and anyone who
    /// knows an address can cause that — so reading a lockout as "disabled" let five wrong passwords
    /// against a colleague's email end their open session. The lockout still refuses the next sign-in;
    /// that is Identity's job, and nothing here undoes it.
    /// </summary>
    [Fact]
    public async Task An_operator_locked_out_by_failed_sign_ins_is_not_disabled_and_still_resolves()
    {
        var id = await CreateAsync();
        await FailSignInsUntilLockedOutAsync(id);

        (await StoredAsync(id)).IsDisabled.ShouldBeFalse();
        (await CookieResolver().ResolveAsync(id.ToString(), null, Ct)).ShouldNotBeNull(
            "failed sign-ins by somebody else must not revoke an operator's open session");
    }

    /// <summary>The other half: a disable is still read as one, and still refused.</summary>
    [Fact]
    public async Task A_disabled_operator_is_disabled_and_does_not_resolve()
    {
        var id = await CreateAsync();

        await AdministerAsync(people => people.SetDisabledAsync(id, disabled: true, Ct));

        (await StoredAsync(id)).IsDisabled.ShouldBeTrue();
        (await CookieResolver().ResolveAsync(id.ToString(), null, Ct)).ShouldBeNull();
    }

    /// <summary>The unguarded administration, resolved from the long-lived scope — the tab's own.</summary>
    /// <returns>The administration the circuit's scope holds.</returns>
    private IAlvoUserAdministration OpenScopeAdministration()
        => _circuit.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);

    /// <summary>Reads an operator as the store holds them now, from a scope of its own.</summary>
    /// <param name="id">The operator.</param>
    /// <returns>The stored operator.</returns>
    private async Task<AlvoUser> StoredAsync(UserId id)
    {
        using var fresh = _provider.CreateScope();
        return (await fresh.ServiceProvider.GetRequiredService<IAlvoUserStore>().FindAsync(id, Ct)).ShouldNotBeNull();
    }

    /// <summary>
    /// Records failed sign-ins until Identity locks the account — exactly what
    /// <c>PasswordSignInAsync(lockoutOnFailure: true)</c> does on a wrong password.
    /// </summary>
    /// <param name="id">The operator somebody is guessing at.</param>
    /// <returns>A task that completes once the account is locked out.</returns>
    private async Task FailSignInsUntilLockedOutAsync(UserId id)
    {
        using var fresh = _provider.CreateScope();
        var users = fresh.ServiceProvider.GetRequiredService<UserManager<AlvoIdentityUser>>();
        var row = (await users.FindByIdAsync(id.Value.ToString())).ShouldNotBeNull();

        while (!await users.IsLockedOutAsync(row))
        {
            (await users.AccessFailedAsync(row)).Succeeded.ShouldBeTrue();
        }
    }

    /// <summary>The cookie resolver, resolved from the long-lived scope.</summary>
    /// <returns>The resolver the dashboard's caller resolution reaches.</returns>
    private IAlvoContextResolver CookieResolver()
        => _circuit.ServiceProvider.GetRequiredKeyedService<IAlvoContextResolver>(AlvoIdentity.ResolverKey);

    /// <summary>Creates an operator holding <c>editor</c>, from a scope of its own.</summary>
    /// <param name="tenant">The tenant to grant, if any.</param>
    /// <param name="email">The operator's address.</param>
    /// <returns>The new operator's identifier.</returns>
    private async Task<UserId> CreateAsync(TenantId? tenant = null, string email = "Eva@Example.test")
    {
        UserId id = default;
        await AdministerAsync(async people =>
            id = (await people.CreateAsync(new AlvoUserCreation(email, ["editor"], tenant), Ct)).Id);
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

    /// <summary>
    /// Lets another administrator's write land between a scope's read and its save — the race a
    /// concurrency stamp exists for, made deterministic.
    /// </summary>
    /// <remarks>
    /// One-shot: the armed write is taken before it runs, so the save it makes itself passes straight
    /// through rather than recursing.
    /// </remarks>
    private sealed class InterleavedWrite : SaveChangesInterceptor
    {
        private Func<Task>? _next;

        /// <summary>Arms one write to run before the next save of any scope.</summary>
        /// <param name="write">The other administrator's write.</param>
        public void Before(Func<Task> write) => _next = write;

        /// <inheritdoc/>
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _next, null) is { } write)
            {
                await write();
            }

            return result;
        }
    }
}
