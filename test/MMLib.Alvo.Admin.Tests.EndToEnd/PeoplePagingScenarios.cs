using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using MMLib.Alvo.Data;

namespace MMLib.Alvo.Admin.Tests.EndToEnd;

/// <summary>
/// People past the first 50 are reachable, by page and by search (docs/todo-admin.md §8d item 23).
/// </summary>
/// <remarks>
/// Fifty-one people are seeded through the unguarded administration — the test standing in for a directory that
/// grew, as <c>RevokedSessionScenarios</c> stands in for a second administrator. The list is ordered by address, so
/// the bootstrap administrator and <c>person-00</c>…<c>person-48</c> fill the first page. Its own world.
/// </remarks>
/// <param name="world">The running host and browser.</param>
public sealed class PeoplePagingScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_next_page_and_a_search_reach_everybody()
    {
        await SeedAsync(count: 51);
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");

        // --- a page turn keeps focus on the pager, on the other button once the pressed one is gone
        (await session.Content.InnerTextAsync()).ShouldNotContain("person-50@alvo.test");
        await session.Page.GetByTestId("people-next").ClickAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync();
        (await session.Content.InnerTextAsync()).ShouldContain("person-50@alvo.test");
        await session.WaitForFocusInsideAsync("people-previous");

        await session.Page.GetByTestId("people-previous").ClickAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusInsideAsync("people-next");
        await session.Page.GetByTestId("people-next").ClickAsync();
        await session.Page.GetByTestId("people-previous").WaitForAsync();

        // --- a search starts from the first page, and ignores case as signing in does
        var search = session.Page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a person by address" });
        await search.FillAsync("PERSON-07");
        await session.Page.GetByTestId("people-previous").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.Content.GetByText("person-08@alvo.test").WaitForAsync(new() { State = WaitForSelectorState.Detached });

        var found = await session.Content.InnerTextAsync();
        found.ShouldContain("person-07@alvo.test");
        found.ShouldNotContain("person-08@alvo.test");

        // --- a search that finds nobody offers the way back, which returns focus to the box
        await search.FillAsync("nobody-matches");
        await session.Page.GetByTestId("people-search-clear").ClickAsync();
        await session.Page.GetByTestId("people-next").WaitForAsync();
        await session.WaitForFocusInsideAsync("people-search");
        (await search.InputValueAsync()).ShouldBeEmpty();

        session.AssertConsoleClean();
    }

    /// <summary>
    /// A person created while the list shows a search they do not match, or a page they do not sort onto, is still
    /// drawn and revealed where the operator is looking (spec §3.5): the list narrows to their address, and says so.
    /// </summary>
    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task A_person_created_off_the_page_on_screen_is_still_shown_and_lit()
    {
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/access");
        var search = session.Page.GetByRole(AriaRole.Searchbox, new() { Name = "Find a person by address" });
        await search.FillAsync("nobody-matches");
        await session.Page.GetByTestId("people-search-clear").WaitForAsync();

        var id = await session.CreatePersonAsync("zz-arrived@alvo.test");

        var row = session.Page.Locator($"#person-{id}");
        await session.WaitForInViewAsync(row);
        (await row.Locator("..").GetAttributeAsync("data-alvo-new")).ShouldBe("true", "a created person is lit");
        (await search.InputValueAsync()).ShouldBe("zz-arrived@alvo.test");
        var revealing = session.Page.GetByTestId("people-revealing");
        (await revealing.InnerTextAsync()).ShouldContain("Showing the person you created");

        await revealing.GetByRole(AriaRole.Button, new() { Name = "Clear the search" }).ClickAsync();
        await revealing.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        (await search.InputValueAsync()).ShouldBeEmpty();
        session.AssertConsoleClean();
    }

    private async Task SeedAsync(int count)
    {
        using var scope = world.Services.CreateScope();
        var people = scope.ServiceProvider.GetRequiredKeyedService<IAlvoUserAdministration>(AlvoUserAdministration.UnguardedKey);
        for (var i = 0; i < count; i++)
        {
            await people.CreateAsync(new AlvoUserCreation(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"person-{i:D2}@alvo.test"), []));
        }
    }
}

/// <summary>
/// The Data grid's pager keeps focus when the pressed button goes with the page it led away from (spec §3.2's
/// "never &lt;body&gt;", applied to a pager; the Access list does the same) — once: a later redraw of the grid never
/// takes focus back to the pager. Its own world: 26 regions and 26 customers, one more than a page each.
/// </summary>
/// <param name="world">The running host and browser.</param>
public sealed class GridPagerFocusScenarios(AdminWorld world) : IClassFixture<AdminWorld>
{
    private static readonly TenantId _tenant = TenantId.New();

    /// <summary>Whether focus is on either pager button.</summary>
    private const string FocusOnThePager
        = "() => !!document.activeElement?.closest(\"[data-testid='grid-next'], [data-testid='grid-previous']\")";

    [Fact(Timeout = AdminWorld.ScenarioTimeout)]
    public async Task The_pager_hands_focus_on_once_and_a_redrawn_grid_does_not_take_it_back()
    {
        await SeedAsync();
        await using var session = await world.SignInAsync(TestContext.Current.CancellationToken);
        await session.GoAsync("/data/regions");

        // --- the last page hands focus from Next to Previous, and the first page back
        await session.Page.GetByTestId("grid-next").ClickAsync();
        await session.Page.GetByTestId("grid-next").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusInsideAsync("grid-previous");
        await session.Page.GetByTestId("grid-previous").ClickAsync();
        await session.Page.GetByTestId("grid-previous").WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await session.WaitForFocusInsideAsync("grid-next");

        // --- a search that empties the grid and fills it again leaves focus in the box being typed into
        await session.Page.GetByTestId("grid-next").ClickAsync();
        await session.WaitForFocusInsideAsync("grid-previous");
        var search = session.Page.GetByTestId("grid-search");
        await search.FillAsync("nothing-is-called-this");
        await session.Page.GetByTestId("grid-row").First.WaitForAsync(new() { State = WaitForSelectorState.Detached });
        await search.FillAsync(string.Empty);
        await session.Page.GetByTestId("grid-next").WaitForAsync();
        await StaysOffThePagerAsync(session, "the search box keeps focus when the rows come back");
        await session.WaitForFocusInsideAsync("grid-search");

        // --- another entity's grid, drawn in place, does not take focus to its pager either
        await session.Page.GetByTestId("grid-next").ClickAsync();
        await session.WaitForFocusInsideAsync("grid-previous");
        await session.Page.EvaluateAsync("path => Blazor.navigateTo(path)", $"{AlvoAdmin.BasePath}/data/customers");
        await session.Page.GetByTestId("grid-next").WaitForAsync();
        await StaysOffThePagerAsync(session, "a new entity's pager is not where focus was");
        session.AssertConsoleClean();
    }

    /// <summary>
    /// Gives a replayed focus move the time it would take (it polls every 20 ms once the pager is drawn), then asserts
    /// it did not happen.
    /// </summary>
    private static async Task StaysOffThePagerAsync(AdminSession session, string because)
    {
        await session.Page.WaitForTimeoutAsync(400);
        (await session.Page.EvaluateAsync<bool>(FocusOnThePager)).ShouldBeFalse(because);
    }

    private async Task SeedAsync()
    {
        using var scope = world.Services.CreateScope();
        await FieldServiceSeed.GrantTheOperatorAsync(scope.ServiceProvider, _tenant);
        var data = scope.ServiceProvider.GetRequiredService<IAlvoData>();
        var system = AlvoContext.System(_tenant);
        for (var i = 0; i < 26; i++)
        {
            var n = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{i:D2}");
            await FieldServiceSeed.RegionAsync(data, system, $"PAGE-{n}");
            await FieldServiceSeed.CustomerAsync(data, system, _tenant, $"Customer {n}");
        }
    }
}
