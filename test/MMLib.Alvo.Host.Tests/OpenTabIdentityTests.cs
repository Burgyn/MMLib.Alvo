using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.Extensions.DependencyInjection;
using MMLib.Alvo.Auth;

namespace MMLib.Alvo.Host.Tests;

/// <summary>
/// What the <b>shipped composition</b> does for a dashboard tab that stays open: its administration
/// writes are each their own unit of work, and its authentication state is re-checked.
/// </summary>
/// <remarks>
/// <para>
/// Both facts are about the arrangement rather than a class — the guard decorator resolving the
/// implementation per call, and <c>AddAlvoIdentityCookieSignIn</c>'s provider being the one the host
/// hands a circuit — so both ask the host's own container, as
/// <see cref="UserAdministrationContractOverIdentityTests"/> does and for its reason.
/// </para>
/// <para>
/// <b>One long-lived scope stands for the circuit</b>, and the public interface is resolved from it
/// exactly as the dashboard's gateway resolves it. The second administrator writes from a scope of
/// their own, through the unguarded key, the way <c>RevokedSessionScenarios</c> stands in for one.
/// </para>
/// </remarks>
public sealed class OpenTabIdentityTests : IAsyncLifetime
{
    private AlvoHostWorld? _world;
    private IServiceScope? _circuit;
    private UserId _administrator;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <inheritdoc/>
    public async ValueTask InitializeAsync()
    {
        _world = await AlvoHostWorld.StartAsync("host-user-admin.alvo.json");
        _circuit = _world.Services.CreateScope();
        _administrator = await CreateAsync("operator@alvo.test", "operator");
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        _circuit?.Dispose();
        if (_world is not null)
        {
            await _world.DisposeAsync();
        }
    }

    /// <summary>
    /// <b>The literal sequence the review found</b>: the tab writes a person, another administrator
    /// changes them, the tab writes them again — and then disables somebody else. Before each call was
    /// its own unit of work the second write was refused for a change the operator never saw, and the
    /// rows it left tracked made the third fail too.
    /// </summary>
    [Fact]
    public async Task A_tab_that_wrote_a_person_changed_elsewhere_can_still_administer_everyone()
    {
        var eva = await CreateAsync("eva@alvo.test");
        var otto = await CreateAsync("otto@alvo.test");
        var moved = TenantId.New();
        var tab = Tab();

        await tab.SetRolesAsync(eva, [], Ct);
        await ElsewhereAsync(people => people.SetTenantAsync(eva, moved, Ct));
        await tab.SetDisabledAsync(eva, disabled: true, Ct);
        await tab.SetDisabledAsync(otto, disabled: true, Ct);

        var stored = await StoredAsync(eva);
        stored.IsDisabled.ShouldBeTrue();
        stored.Tenant.ShouldBe(moved, "the other administrator's write is kept, not overwritten by the tab's copy");
        (await StoredAsync(otto)).IsDisabled.ShouldBeTrue();
    }

    /// <summary>
    /// The host hands a circuit the revalidating provider, whichever of the identity package and the
    /// dashboard registered first — <c>AddRazorComponents</c> registers its plain one too.
    /// </summary>
    [Fact]
    public void The_host_revalidates_an_open_circuits_authentication_state()
        => _circuit!.ServiceProvider.GetRequiredService<AuthenticationStateProvider>()
            .ShouldBeAssignableTo<RevalidatingServerAuthenticationStateProvider>();

    /// <summary>The public interface, from the circuit's scope, acting as the administrator.</summary>
    /// <returns>The guarded administration the dashboard's gateway holds.</returns>
    private IAlvoUserAdministration Tab()
    {
        var services = _circuit!.ServiceProvider;
        services.GetRequiredService<IAlvoContextAccessor>().Principal = new AlvoPrincipal
        {
            Context = new AlvoContext
            {
                User = _administrator,
                Roles = new HashSet<Role> { RoleCatalog.Create(["operator"]).Get("operator") },
            },
            Scopes = new HashSet<ApiKeyScope>(),
            KeyId = $"test:{_administrator}",
        };

        return services.GetRequiredService<IAlvoUserAdministration>();
    }

    /// <summary>Creates a person through the unguarded implementation, from a scope of its own.</summary>
    /// <param name="email">Their address.</param>
    /// <param name="roleNames">Their roles.</param>
    /// <returns>Their identifier.</returns>
    private async Task<UserId> CreateAsync(string email, params string[] roleNames)
    {
        UserId id = default;
        await ElsewhereAsync(async people =>
            id = (await people.CreateAsync(new AlvoUserCreation(email, roleNames), Ct)).Id);
        return id;
    }

    /// <summary>Runs one call as a second administrator would: in a scope that is not the circuit's.</summary>
    /// <param name="write">The call.</param>
    /// <returns>A task that completes when the scope has been disposed.</returns>
    private async Task ElsewhereAsync(Func<IAlvoUserAdministration, Task> write)
    {
        using var other = _world!.Services.CreateScope();
        await write(other.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey));
    }

    /// <summary>A person as the store holds them now, read from a scope of its own.</summary>
    /// <param name="id">The person.</param>
    /// <returns>The stored person.</returns>
    private async Task<AlvoUser> StoredAsync(UserId id)
    {
        using var fresh = _world!.Services.CreateScope();
        return (await fresh.ServiceProvider.GetRequiredService<IAlvoUserStore>().FindAsync(id, Ct)).ShouldNotBeNull();
    }
}
