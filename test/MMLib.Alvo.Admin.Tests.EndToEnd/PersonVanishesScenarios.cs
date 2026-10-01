using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using System.Collections.Concurrent;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A world where a person can vanish from the membership store while a screen still shows them: the one race the
/// port can lose today, since it has no delete (<c>ManagementGateway.ChangeRolesAsync</c> reads the person fresh
/// and refuses when they are gone).
/// </summary>
/// <remarks>
/// <b>The one service this world stands in for</b> is the guarded <see cref="IAlvoUserAdministration"/>, wrapped
/// so its list leaves out whoever <see cref="Hide"/> named. Every write still goes to the real store.
/// </remarks>
public sealed class VanishingPeopleWorld : AdminWorld
{
    private readonly ConcurrentDictionary<string, bool> _hidden = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Makes <paramref name="email"/> disappear from every list from now on.</summary>
    /// <param name="email">Whose address.</param>
    public void Hide(string email) => _hidden[email] = true;

    /// <inheritdoc/>
    protected override void Configure(IServiceCollection services)
    {
        var shipped = services.Last(entry => entry.ServiceType == typeof(IAlvoUserAdministration) && !entry.IsKeyedService);
        services.Remove(shipped);
        services.Add(new ServiceDescriptor(
            typeof(IAlvoUserAdministration),
            provider => new Hiding(Build(shipped, provider), _hidden),
            shipped.Lifetime));
    }

    private static IAlvoUserAdministration Build(ServiceDescriptor shipped, IServiceProvider provider)
        => (IAlvoUserAdministration)(shipped.ImplementationFactory?.Invoke(provider)
            ?? shipped.ImplementationInstance
            ?? ActivatorUtilities.CreateInstance(provider, shipped.ImplementationType!));

    /// <summary>The shipped port, with the hidden people left out of its list.</summary>
    private sealed class Hiding(IAlvoUserAdministration inner, ConcurrentDictionary<string, bool> hidden)
        : IAlvoUserAdministration
    {
        public async Task<AlvoUserPage> ListAsync(AlvoUserQuery query, CancellationToken cancellationToken = default)
        {
            var page = await inner.ListAsync(query, cancellationToken).ConfigureAwait(false);
            return page with { Users = [.. page.Users.Where(person => !hidden.ContainsKey(person.Email))] };
        }

        public Task<AlvoUser> CreateAsync(AlvoUserCreation creation, CancellationToken cancellationToken = default)
            => inner.CreateAsync(creation, cancellationToken);

        public Task<AlvoUser> SetRolesAsync(
            UserId user, IReadOnlyList<string> roleNames, CancellationToken cancellationToken = default)
            => inner.SetRolesAsync(user, roleNames, cancellationToken);

        public Task<AlvoUser> SetTenantAsync(UserId user, TenantId? tenant, CancellationToken cancellationToken = default)
            => inner.SetTenantAsync(user, tenant, cancellationToken);

        public Task<AlvoUser> SetDisabledAsync(UserId user, bool disabled, CancellationToken cancellationToken = default)
            => inner.SetDisabledAsync(user, disabled, cancellationToken);

        public Task<AlvoUser> ClearLockoutAsync(UserId user, CancellationToken cancellationToken = default)
            => inner.ClearLockoutAsync(user, cancellationToken);

        public Task<AlvoCredentialToken> IssueCredentialTokenAsync(UserId user, CancellationToken cancellationToken = default)
            => inner.IssueCredentialTokenAsync(user, cancellationToken);
    }
}

/// <summary>
/// A person who vanished while their editor was open: the save is refused in place, with focus and a Reload that
/// reads the list again (spec §3.3), and nothing claims it worked.
/// </summary>
/// <param name="world">The running host, whose people can vanish.</param>
public sealed class PersonVanishesScenarios(VanishingPeopleWorld world) : IClassFixture<VanishingPeopleWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_save_for_a_person_who_vanished_is_refused_with_focus_and_Reload_reads_the_list_again()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");
        await session.Page.GetByTestId("person-new").ClickAsync();
        await session.Page.FillAsync("#new-person-email", "gone@example.com");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.SnackbarAsync("Created gone@example.com");
        var row = session.Content.Locator("[id^='person-']").Filter(new() { HasText = "gone@example.com" });
        var id = (await row.GetAttributeAsync("id"))!;

        await session.Page.ClickAsync($"#change-{id["person-".Length..]}");
        var editor = session.Dialog("person-editor");
        await editor.GetByRole(AriaRole.Button, new() { Name = "dispatcher", Exact = true }).ClickAsync();
        world.Hide("gone@example.com");
        await editor.GetByTestId("person-save").ClickAsync();

        await editor.GetByTestId("error-panel").WaitForAsync();
        await session.WaitForFocusInsideAsync("error-panel");
        (await session.SnackbarCountAsync("Saved")).ShouldBe(0, "a refused save never says it saved");

        await editor.GetByTestId("person-reload").ClickAsync();
        await editor.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Page.Locator($"#{id}").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await session.SnackbarCountAsync("Saved")).ShouldBe(0);
        session.AssertConsoleClean();
    }
}
