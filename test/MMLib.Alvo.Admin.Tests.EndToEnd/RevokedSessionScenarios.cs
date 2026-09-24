using MMLib.Alvo.Auth;
using MMLib.Alvo.Management;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// A session that outlives the account it names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Found by running the thing, not by reading it.</b> A cookie survives the database that issued
/// it, so a developer who deletes the SQLite file and restarts — which is the ordinary inner loop —
/// comes back to a dashboard that renders its whole chrome and refuses every screen. The refusal is
/// correct; what was missing was any way out of it, because the only sign-out control lives in the
/// shell's account menu, around the screen rather than on it.
/// </para>
/// <para>
/// This scenario reaches the same state the supported way — disabling the operator — and asserts
/// both halves: the refusal is rendered, and it carries the control that resolves it.
/// </para>
/// <para>
/// <b>Its own world, and it ends by locking that world out.</b> Disabling the bootstrap
/// administrator of a project whose <c>access</c> block admits nobody else leaves nobody who can
/// manage it, which is exactly why no other scenario may share this fixture.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class RevokedSessionScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_session_whose_account_is_gone_is_offered_the_way_out()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("");

        await SetOperatorDisabledAsync(world, disabled: true);

        await session.GoAsync("");
        await session.Page.GetByText("not allowed").First.WaitForAsync();

        var exit = session.Page.Locator("[data-testid='forbidden-sign-out']");
        (await exit.CountAsync()).ShouldBeGreaterThan(0, "a refusal with no control on the screen is a dead end");

        await exit.First.ClickAsync();
        await session.Page.WaitForURLAsync("**/admin/sign-in**");
    }

    /// <summary>
    /// Bars (or restores) the signed-in administrator, through the store rather than through a screen.
    /// </summary>
    /// <remarks>
    /// The unguarded registration, because the guarded one asks who is calling and this call has no
    /// caller — it is the test standing in for the second administrator a real deployment would
    /// have. It runs in a scope of its own, so it never shares a change tracker with any circuit.
    /// </remarks>
    /// <param name="world">The running host.</param>
    /// <param name="disabled">Whether the operator ends up disabled.</param>
    internal static async Task SetOperatorDisabledAsync(AdminWorld world, bool disabled)
    {
        using var scope = world.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(
            AlvoUserAdministration.UnguardedKey);

        var page = await people.ListAsync(new AlvoUserQuery());
        var operatorAccount = page.Users.Single(
            person => string.Equals(person.Email, AdminWorld.AdminEmail, StringComparison.OrdinalIgnoreCase));

        await people.SetDisabledAsync(operatorAccount.Id, disabled);
    }
}

/// <summary>
/// <b>A disable takes effect in a tab that is already open</b> — on the operator's very next call, not on
/// their next page load (design §2.7: "role membership, a person's tenant, disabled | the identity store |
/// at once").
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this pins</b>
/// (<c>docs/superpowers/specs/evidence/2026-09-25-disabled-operator-stale-identity.md</c>): the circuit's
/// identity <c>DbContext</c> lives as long as the tab, and the user store read the operator through a
/// tracked lookup, so every later resolution was answered from the change tracker. A disabled operator
/// kept applying from the tab they already had open.
/// </para>
/// <para>
/// <b>No <c>GoAsync</c> between the disable and the click</b>, which is the whole scenario: a page load
/// starts a new circuit and a new <c>DbContext</c>, and would pass with the defect in place — that is why
/// <see cref="RevokedSessionScenarios"/> never caught it. <b>Its own world</b>, because it locks its
/// operator out; it restores them only to read the revision back.
/// </para>
/// <para>
/// No tenant-move twin here: the bootstrap administrator is authorized above the descriptor whatever
/// tenant they hold, so a move changes nothing an Apply could show. The tenant half is pinned at the
/// store and the resolver, in <c>AlvoIdentityLongLivedScopeTests</c>.
/// </para>
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class OpenCircuitRevocationScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task An_operator_disabled_while_their_tab_is_open_cannot_apply_from_it()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        var before = await CurrentRevisionAsync();
        await session.GoAsync("/schema");
        await session.Button("New entity", exact: true).ClickAsync();
        await session.Page.FillAsync("#new-entity-name", "tickets");
        await session.Page.Keyboard.PressAsync("Enter");
        await session.Page.WaitForURLAsync("**/schema/tickets");
        await session.PreviewPendingAsync();
        await session.Page.FillAsync("#apply-reason", "Add tickets");

        await RevokedSessionScenarios.SetOperatorDisabledAsync(world, disabled: true);
        await session.Button("Apply these changes").ClickAsync();

        /* Whichever answer comes first, so the defect fails on what it did rather than on a timeout. */
        var applied = session.Page.GetByText("Applied as revision").First;
        await session.Page.GetByTestId("error-panel").Or(applied).First.WaitForAsync();
        (await applied.CountAsync()).ShouldBe(0, "a disabled operator's apply must be refused, not written");
        await session.Page.GetByTestId("error-panel").WaitForAsync();
        await RevokedSessionScenarios.SetOperatorDisabledAsync(world, disabled: false);
        (await CurrentRevisionAsync()).ShouldBe(before, "the refused apply must not have appended a revision");
    }

    /// <summary>
    /// The project's current revision, read through the management contract as the administrator — who
    /// must be enabled at the time, since that is the very check this class is about.
    /// </summary>
    /// <returns>The revision number.</returns>
    private async Task<int> CurrentRevisionAsync()
    {
        using var scope = world.Services.CreateScope();
        var services = scope.ServiceProvider;
        var ambient = services.GetRequiredService<IAlvoContextAccessor>();
        ambient.Principal = await ApplyScenarioSteps.AdministratorAsync(services);
        try
        {
            var management = services.GetRequiredService<IAlvoManagement>();
            var project = (await management.ListProjectsAsync())[0].Name;
            return (await management.GetDescriptorAsync(project)).Revision;
        }
        finally
        {
            ambient.Principal = null;
        }
    }
}
